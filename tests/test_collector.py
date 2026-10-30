import csv
import io
import json
import sys
import tempfile
import unittest
import urllib.error
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parents[1] / 'AutoScript/Resources/collector'))
from youtube_collector import collect, channel_selector, YouTube, channel_lock
from transcript_sources import CollectorError, NoTranscript, Supadata, Http, render
from vidiq_provider import VidiqMcp

CHANNEL = 'UC' + 'a' * 22
VIDS = ['video000001', 'video000002', 'video000003', 'video000004']


class Api:
    def __init__(self):
        self.calls = []
        self.bad_token = False

    def request(self, method, url, headers=None, body=None):
        from urllib.parse import urlsplit, parse_qs
        self.calls.append((url, headers))
        query = parse_qs(urlsplit(url).query)
        if '/channels?' in url:
            return {'items': [{'id': CHANNEL, 'snippet': {'title': 'A channel'}, 'contentDetails': {'relatedPlaylists': {'uploads': 'uploads'}}}]}, {}
        if '/playlistItems?' in url:
            ids = VIDS[:3] if 'pageToken' not in query else [VIDS[0], VIDS[3]]
            page = {'items': [{'contentDetails': {'videoId': v}} for v in ids]}
            if 'pageToken' not in query or self.bad_token:
                page['nextPageToken'] = 'next'
            return page, {}
        if '/videos?' in url:
            return {'items': [{'id': v, 'status': {'privacyStatus': 'private' if v == VIDS[2] else 'public'},
                               'snippet': {'title': '=Untrusted title' if v == VIDS[0] else v, 'publishedAt': '2020-01-01T00:00:00Z'},
                               'statistics': {'viewCount': '123'}, 'contentDetails': {'duration': 'PT2M'}}
                              for v in query['id'][0].split(',')]}, {}
        raise AssertionError('Unexpected API request')


class Source:
    def __init__(self):
        self.calls = []
        self.fail = True
        self.missing = True
        self.after_fetch = None

    def fetch(self, video):
        self.calls.append(video)
        if self.after_fetch:
            self.after_fetch(video)
        if video == VIDS[1] and self.fail:
            raise CollectorError('Temporary test failure')
        if video == VIDS[3] and self.missing:
            raise NoTranscript('No captions')
        return render([{'start': 1.25, 'text': 'Nội dung gốc.\nGiữ nguyên lời.'}]), 'vi'


class CollectorTests(unittest.TestCase):
    def config(self, root):
        return dict(channel_url='https://www.youtube.com/@example', youtube_key='test-only', output_root=root)

    def test_url_resolution_and_validation(self):
        self.assertEqual(channel_selector('https://youtube.com/@Test/videos'), ('forHandle', '@Test'))
        self.assertEqual(channel_selector('https://youtube.com/channel/' + CHANNEL), ('id', CHANNEL))
        self.assertEqual(channel_selector('https://youtube.com/user/oldname'), ('forUsername', 'oldname'))
        for url in ['https://youtube.com.evil/@test', 'https://evil/youtube.com/@test', 'file:///etc/passwd', 'https://youtube.com/watch?v=x', 'https://youtube.com/c/custom']:
            with self.subTest(url=url), self.assertRaises(CollectorError):
                channel_selector(url)

    def test_pagination_dedup_public_filter_metadata(self):
        api = Api()
        rows = YouTube('test-only', api).videos('uploads')
        self.assertEqual([r['video_id'] for r in rows], [VIDS[0], VIDS[1], VIDS[3]])
        self.assertEqual(rows[0]['duration'], 'PT2M')
        self.assertEqual(rows[0]['views'], '123')
        self.assertTrue(all('key=' not in url for url, _ in api.calls))
        self.assertTrue(all(headers['X-Goog-Api-Key'] == 'test-only' for _, headers in api.calls))

    def test_detect_repeated_pagination_token(self):
        api = Api(); api.bad_token = True
        with self.assertRaises(CollectorError):
            YouTube('test-only', api).videos('uploads')

    def test_checkpoint_each_video_resume_and_repair_missing_files(self):
        with tempfile.TemporaryDirectory() as root:
            source, events = Source(), []
            def checkpoint(video):
                if video == VIDS[1]:
                    state_file = Path(root) / CHANNEL / 'state.json'
                    self.assertEqual(json.loads(state_file.read_text(encoding='utf-8'))[VIDS[0]]['status'], 'success')
            source.after_fetch = checkpoint
            result = collect(self.config(root), Api(), source, events.append)
            folder = Path(result['folder'])
            self.assertEqual((result['total'], result['success'], result['failed'], result['remaining']), (3, 1, 2, 0))
            transcript = folder / 'transcripts' / (VIDS[0] + '.txt')
            self.assertEqual(transcript.read_text(encoding='utf-8'), '[00:00:01.250] Nội dung gốc.\nGiữ nguyên lời.')
            with open(folder / 'videos.csv', encoding='utf-8-sig', newline='') as f:
                rows = list(csv.DictReader(f))
            self.assertNotIn(b'\r\r\n', (folder / 'videos.csv').read_bytes())
            self.assertEqual(rows[0]['title'], "'=Untrusted title")
            self.assertEqual(rows[1]['status'], 'failed')
            self.assertEqual(rows[2]['status'], 'no_transcript')
            self.assertNotIn('test-only', (folder / 'state.json').read_text())
            with open(folder / 'failed_videos.csv', encoding='utf-8-sig', newline='') as f:
                self.assertEqual(len(list(csv.DictReader(f))), 2)
            # Before the second video on retry, the first is already durably marked successful.
            source.fail = source.missing = False; source.calls.clear()
            source.after_fetch = lambda _: self.assertEqual(json.loads((folder / 'state.json').read_text())[VIDS[0]]['status'], 'success')
            result = collect(self.config(root), Api(), source)
            self.assertEqual(source.calls, [VIDS[1], VIDS[3]])
            self.assertEqual(result['success'], 3)
            # Fully cached: no transcript key or provider construction needed.
            self.assertEqual(collect(self.config(root), Api())['success'], 3)
            transcript.write_text('Tampered')
            source.calls.clear()
            collect(self.config(root), Api(), source)
            self.assertEqual(source.calls, [VIDS[0]])
            transcript.unlink()
            source.calls.clear()
            collect(self.config(root), Api(), source)
            self.assertEqual(source.calls, [VIDS[0]])
            self.assertTrue(all(e['remaining'] >= 0 for e in events if 'remaining' in e))

    def test_unknown_exception_does_not_stop_other_videos_or_leak(self):
        with tempfile.TemporaryDirectory() as root:
            class Broken:
                def fetch(self, video):
                    if video == VIDS[0]:
                        raise ValueError('Secret vendor response')
                    return '[00:00:00.000] Original text', 'en'
            result = collect(self.config(root), Api(), Broken())
            self.assertEqual((result['success'], result['failed']), (2, 1))
            self.assertNotIn('Secret vendor response', Path(result['folder'], 'failed_videos.csv').read_text(encoding='utf-8-sig'))

    def test_lock_and_corrupt_state_protect_existing_data(self):
        with tempfile.TemporaryDirectory() as root:
            folder = Path(root) / CHANNEL; folder.mkdir()
            with channel_lock(folder):
                with self.assertRaises(CollectorError):
                    collect(self.config(root), Api(), Source())
            (folder / 'state.json').write_text('broken')
            with self.assertRaises(CollectorError):
                collect(self.config(root), Api(), Source())
            self.assertEqual((folder / 'state.json').read_text(), 'broken')

    def test_preserve_unicode_and_reject_missing_timestamps(self):
        self.assertEqual(render([{'start': 3601.125, 'text': 'Tiếng Việt 原文'}]), '[01:00:01.125] Tiếng Việt 原文')
        with self.assertRaises(CollectorError):
            render([{'text': 'Missing timestamp'}])
        with self.assertRaises(NoTranscript):
            render([])


class ProviderTests(unittest.TestCase):
    def test_supadata_contract_native_no_translation_no_generation(self):
        class Transport:
            def request(self, method, url, headers, body=None):
                from urllib.parse import urlsplit, parse_qs
                q = parse_qs(urlsplit(url).query)
                assert urlsplit(url).path == '/v1/transcript'
                assert q['mode'] == ['native'] and q['text'] == ['false'] and 'lang' not in q
                assert headers == {'x-api-key': 'test-key'}
                return {'content': [{'offset': 1250, 'duration': 100, 'text': 'Original.'}], 'lang': 'en'}, {}
        self.assertEqual(Supadata('test-key', Transport()).fetch(VIDS[0]), ('[00:00:01.250] Original.', 'en'))

    def test_supadata_native_async_job(self):
        class Transport:
            calls = 0
            def sleep(self, seconds):
                pass
            def request(self, method, url, headers, body=None):
                self.calls += 1
                if self.calls == 1:
                    return {'jobId': 'job-1'}, {}
                if self.calls == 2:
                    return {'status': 'active'}, {}
                return {'status': 'completed', 'result': {'lang': 'vi', 'content': [{'offset': 0, 'text': 'Gốc'}]}}, {}
        self.assertEqual(Supadata('test-key', Transport()).fetch(VIDS[0]), ('[00:00:00.000] Gốc', 'vi'))

    def test_http_retries_and_does_not_leak_response(self):
        calls, delays = [], []
        def failure(req, timeout):
            calls.append(req)
            raise urllib.error.HTTPError(req.full_url, 429, 'Rate limit', {}, io.BytesIO(b'{"secret":"do-not-log"}'))
        with self.assertRaises(CollectorError) as caught:
            Http(failure, delays.append).request('GET', 'https://api.example.test/data')
        self.assertEqual(len(calls), 3)
        self.assertEqual(delays, [2, 4])
        self.assertNotIn('do-not-log', str(caught.exception))
        def denied(req, timeout):
            raise urllib.error.HTTPError(req.full_url, 403, 'Denied', {}, io.BytesIO())
        with self.assertRaises(CollectorError):
            Http(denied, lambda _: self.fail('Must not retry 403')).request('GET', 'https://api.example.test/data')

    def test_mcp_sse_stops_at_result_without_waiting_for_eof(self):
        class Response:
            headers = {'Content-Type': 'text/event-stream'}
            def __enter__(self): return self
            def __exit__(self, *args): pass
            def __iter__(self):
                yield b': heartbeat\n'
                yield b'data: {"jsonrpc":"2.0","id":1,"result":{"tools":[]}}\n'
                yield b'\n'
                raise AssertionError('Must return as soon as the response is complete')
        result, _ = Http(lambda req, timeout: Response()).request('POST', 'https://mcp.example.test/mcp', body={'id': 1})
        self.assertEqual(result['result']['tools'], [])

    def test_native_unavailable_error(self):
        def unavailable(req, timeout):
            raise urllib.error.HTTPError(req.full_url, 400, 'Bad request', {}, io.BytesIO(b'{"error":"transcript-unavailable"}'))
        with self.assertRaises(NoTranscript):
            Http(unavailable).request('GET', 'https://api.example.test/data')

    def test_mcp_discovers_tool_and_uses_actual_schema(self):
        class Mcp:
            methods = []
            def request(self, method, url, headers, body=None):
                self.methods.append(body['method'])
                if body['method'] == 'notifications/initialized':
                    return {}, {}
                result = {}
                if body['method'] == 'initialize':
                    result = {'protocolVersion': '2025-03-26'}
                elif body['method'] == 'tools/list':
                    result = {'tools': [{'name': 'vidiq_video_transcript', 'inputSchema': {'properties': {'video_id': {'type': 'string'}}, 'required': ['video_id']}}]}
                else:
                    assert headers['Mcp-Session-Id'] == 'session-test'
                    assert body['params']['arguments'] == {'video_id': VIDS[0]}
                    result = {'structuredContent': {'segments': [{'start': 0, 'text': 'Source'}], 'language': 'en'}}
                return {'jsonrpc': '2.0', 'id': body['id'], 'result': result}, {'Mcp-Session-Id': 'session-test'}
        transport = Mcp()
        provider = VidiqMcp('https://mcp.example.test/mcp', 'test-key', transport)
        self.assertEqual(provider.fetch(VIDS[0]), ('[00:00:00.000] Source', 'en'))
        self.assertEqual(transport.methods, ['initialize', 'notifications/initialized', 'tools/list', 'tools/call'])

    def test_mcp_does_not_invent_tool_or_endpoint(self):
        with self.assertRaises(CollectorError):
            VidiqMcp('', '', None)
        class WrongTool:
            def request(self, method, url, headers, body=None):
                if body['method'] == 'notifications/initialized':
                    return {}, {}
                result = {'protocolVersion': '2024-11-05'} if body['method'] == 'initialize' else {'tools': []}
                return {'id': body['id'], 'result': result}, {}
        with self.assertRaises(CollectorError):
            VidiqMcp('https://mcp.example.test/mcp', '', WrongTool())


if __name__ == '__main__':
    unittest.main()
