"""Replaceable transcript provider: fetch(video_id) -> (timestamped original text, language).
Supadata contract checked against official @supadata/js 1.4.0 (SDK and types).
"""
import json
import math
import re
import time
import urllib.error
import urllib.parse
import urllib.request

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise CollectorError('Endpoint chuyển hướng ngoài dự kiến; không chuyển tiếp API key.')

class CollectorError(Exception):
    pass


class NoTranscript(CollectorError):
    pass


class Http:
    def __init__(self, opener=None, sleep=time.sleep):
        self.opener = opener or urllib.request.build_opener(NoRedirect()).open
        self.sleep = sleep

    def request(self, method, url, headers=None, body=None):
        if urllib.parse.urlsplit(url).scheme != 'https':
            raise CollectorError('Endpoint phải dùng HTTPS.')
        data = None if body is None else json.dumps(body).encode('utf-8')
        req = urllib.request.Request(url, data=data, method=method,
                                     headers={'Accept': 'application/json, text/event-stream',
                                              'Content-Type': 'application/json', **(headers or {})})
        for attempt in range(3):
            try:
                with self.opener(req, timeout=60) as response:
                    if 'text/event-stream' in response.headers.get('Content-Type', ''):
                        data_lines = []
                        for raw_line in response:
                            line = raw_line.decode('utf-8-sig').rstrip('\r\n')
                            if line.startswith('data:'):
                                data_lines.append(line[5:].lstrip())
                            elif not line and data_lines:
                                value = json.loads('\n'.join(data_lines))
                                data_lines = []
                                if isinstance(value, dict) and ('result' in value or 'error' in value):
                                    return value, dict(response.headers)
                        raise CollectorError('MCP không trả kết quả trong SSE.')
                    raw = response.read().decode('utf-8-sig')
                    if not raw.strip():
                        return {}, dict(response.headers)
                    return json.loads(raw), dict(response.headers)
            except urllib.error.HTTPError as exc:
                code = exc.code
                try:
                    detail = json.loads(exc.read(65536).decode('utf-8'))
                except (ValueError, UnicodeDecodeError):
                    detail = {}
                if isinstance(detail, dict) and detail.get('error') == 'transcript-unavailable':
                    exc.close()
                    raise NoTranscript('Video không có native transcript.') from None
                retry_after = exc.headers.get('Retry-After', '')
                exc.close()
                if code in (408, 429, 500, 502, 503, 504) and attempt < 2:
                    self.sleep(min(30, int(retry_after)) if retry_after.isdigit() else 2 ** (attempt + 1))
                    continue
                if code == 404:
                    raise NoTranscript('HTTP 404: không tìm thấy transcript hoặc endpoint.') from None
                if code in (401, 403):
                    raise CollectorError(f'HTTP {code}: kiểm tra key, quyền, quota và chính sách mạng.') from None
                raise CollectorError(f'Nguồn dữ liệu trả HTTP {code}.') from None
            except (urllib.error.URLError, TimeoutError, OSError):
                if attempt < 2:
                    self.sleep(2 ** (attempt + 1))
                    continue
                raise CollectorError('Không kết nối được nguồn dữ liệu sau ba lần thử.') from None
            except (json.JSONDecodeError, UnicodeDecodeError):
                raise CollectorError('Nguồn dữ liệu trả nội dung không phải JSON hợp lệ.') from None



def timestamp(seconds):
    if not isinstance(seconds, (int, float)) or not math.isfinite(seconds) or seconds < 0 or seconds > 315360000:
        raise CollectorError('Timestamp transcript không hợp lệ.')
    ms = round(seconds * 1000)
    hours, ms = divmod(ms, 3600000)
    minutes, ms = divmod(ms, 60000)
    secs, ms = divmod(ms, 1000)
    return f'[{hours:02}:{minutes:02}:{secs:02}.{ms:03}]'


def render(segments):
    if not isinstance(segments, list) or not segments:
        raise NoTranscript('Video không có transcript.')
    lines = []
    for item in segments:
        if not isinstance(item, dict) or not isinstance(item.get('text'), str) or 'start' not in item:
            raise CollectorError('Transcript không đúng schema segments: start (giây), text.')
        # Preserve the original text, language and order; only prepend timestamps.
        lines.append(timestamp(item['start']) + ' ' + item['text'])
    if not any(x['text'].strip() for x in segments):
        raise NoTranscript('Transcript trống.')
    return '\n'.join(lines)


class Supadata:
    """Native transcripts only. No AI generation, translation or summarization."""
    def __init__(self, key, http):
        if not key:
            raise CollectorError('Cần Supadata API key trong cấu hình.')
        self.key, self.http = key, http

    def fetch(self, vid):
        query = urllib.parse.urlencode({'url': 'https://www.youtube.com/watch?v=' + vid, 'text': 'false', 'mode': 'native'})
        value, _ = self.http.request('GET', 'https://api.supadata.ai/v1/transcript?' + query, {'x-api-key': self.key})
        if isinstance(value, dict) and isinstance(value.get('jobId'), str):
            job = value['jobId']
            if not re.fullmatch(r'[A-Za-z0-9_-]{1,200}', job):
                raise CollectorError('Supadata job ID không hợp lệ.')
            for _ in range(30):
                self.http.sleep(2)
                status, _ = self.http.request('GET', 'https://api.supadata.ai/v1/transcript/' + job, {'x-api-key': self.key})
                if not isinstance(status, dict):
                    raise CollectorError('Supadata job trả dữ liệu không hợp lệ.')
                if status.get('status') == 'completed':
                    value = status.get('result')
                    break
                if status.get('status') == 'failed':
                    raise CollectorError('Supadata native transcript job thất bại.')
            else:
                raise CollectorError('Supadata native transcript chưa sẵn sàng; chạy lại để thử lại video.')
        if not isinstance(value, dict) or 'error' in value:
            raise CollectorError('Supadata chưa trả native transcript. Kiểm tra quyền/gói API.')
        content = value.get('content')
        if content is None or content == []:
            raise NoTranscript('Không có native transcript.')
        if not isinstance(content, list):
            raise CollectorError('Nguồn không trả transcript kèm timestamp.')
        segments = []
        for row in content:
            if not isinstance(row, dict) or not isinstance(row.get('offset'), (int, float)):
                raise CollectorError('Supadata trả schema transcript không hỗ trợ.')
            segments.append({'start': row['offset'] / 1000, 'text': row.get('text')})
        return render(segments), value.get('lang', '')
