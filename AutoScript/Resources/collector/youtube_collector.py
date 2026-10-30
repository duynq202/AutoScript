"""Public-channel collector. Standard library only; no AI generation or private-video access.
Credentials arrive on stdin, never command arguments or output files.
"""
import csv
import hashlib
import io
import json
import os
import re
import sys
import urllib.parse
from contextlib import contextmanager
from pathlib import Path

# Isolated bundled Python excludes the script directory from sys.path.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from transcript_sources import CollectorError, NoTranscript, Http, Supadata



def channel_selector(url):
    parsed = urllib.parse.urlsplit(url.strip())
    if parsed.scheme not in ('http', 'https') or parsed.hostname not in ('youtube.com', 'www.youtube.com', 'm.youtube.com') or parsed.username or parsed.password:
        raise CollectorError('Nhập URL kênh youtube.com/@handle hoặc youtube.com/channel/UC…')
    parts = [urllib.parse.unquote(x) for x in parsed.path.split('/') if x]
    if parts and parts[0].startswith('@') and len(parts[0]) > 1:
        return 'forHandle', parts[0]
    if len(parts) >= 2 and parts[0] == 'channel' and re.fullmatch(r'UC[A-Za-z0-9_-]{22}', parts[1]):
        return 'id', parts[1]
    if len(parts) >= 2 and parts[0] == 'user' and parts[1]:
        return 'forUsername', parts[1]
    raise CollectorError('URL /c/ không ánh xạ chính xác qua Data API. Dùng URL @handle hoặc /channel/ChannelID của kênh đó.')


class YouTube:
    def __init__(self, key, http):
        if not key:
            raise CollectorError('Cần YouTube Data API key trong cấu hình.')
        self.key, self.http = key, http

    def get(self, resource, **params):
        value, _ = self.http.request('GET', 'https://www.googleapis.com/youtube/v3/' + resource + '?' + urllib.parse.urlencode(params),
                                     {'X-Goog-Api-Key': self.key})
        if not isinstance(value, dict) or 'error' in value:
            raise CollectorError('YouTube Data API từ chối yêu cầu.')
        return value

    def resolve(self, url):
        kind, value = channel_selector(url)
        data = self.get('channels', part='id,snippet,contentDetails', **{kind: value})
        items = data.get('items', [])
        if len(items) != 1:
            raise CollectorError('Không tìm thấy kênh công khai từ URL này.')
        item = items[0]
        channel_id = item.get('id', '')
        if not re.fullmatch(r'UC[A-Za-z0-9_-]{22}', channel_id):
            raise CollectorError('Channel ID trả về không hợp lệ.')
        try:
            return channel_id, item['snippet']['title'], item['contentDetails']['relatedPlaylists']['uploads']
        except KeyError:
            raise CollectorError('Không lấy được uploads playlist của kênh.') from None

    def videos(self, uploads, progress=lambda message: None):
        result, seen, tokens = [], set(), set()
        token = ''
        while True:
            page = self.get('playlistItems', part='contentDetails', playlistId=uploads, maxResults=50, **({'pageToken': token} if token else {}))
            ids = []
            for item in page.get('items', []):
                vid = item.get('contentDetails', {}).get('videoId', '')
                if re.fullmatch(r'[A-Za-z0-9_-]{11}', vid) and vid not in seen:
                    seen.add(vid)
                    ids.append(vid)
            if ids:
                detail = self.get('videos', part='snippet,statistics,contentDetails,status', id=','.join(ids), maxResults=50)
                details = {x.get('id'): x for x in detail.get('items', [])}
                for vid in ids:
                    video = details.get(vid)
                    # Missing/private/unlisted videos are not collected.
                    if not video or video.get('status', {}).get('privacyStatus') != 'public':
                        continue
                    snippet = video.get('snippet', {})
                    result.append(dict(video_id=vid, title=snippet.get('title', ''), url='https://www.youtube.com/watch?v=' + vid,
                                       published_at=snippet.get('publishedAt', ''), views=video.get('statistics', {}).get('viewCount', ''),
                                       duration=video.get('contentDetails', {}).get('duration', '')))
            progress(f'Đã tìm thấy {len(result)} video công khai…')
            token = page.get('nextPageToken')
            if not token:
                return result
            if token in tokens:
                raise CollectorError('YouTube lặp pagination token; chưa có danh sách đầy đủ.')
            tokens.add(token)



def atomic_text(path, text, encoding='utf-8'):
    path = Path(path)
    temp = path.with_name(path.name + '.tmp')
    try:
        temp.write_text(text, encoding=encoding, newline='')
        os.replace(temp, path)
    finally:
        temp.unlink(missing_ok=True)


@contextmanager
def channel_lock(folder):
    handle = open(folder / '.collector.lock', 'a+b')
    try:
        handle.seek(0)
        if os.name == 'nt':
            import msvcrt
            if not handle.read(1):
                handle.write(b'0'); handle.flush()
            handle.seek(0)
            try:
                msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
            except OSError:
                raise CollectorError('Kênh đang được một tác vụ khác xử lý.') from None
        else:
            import fcntl
            try:
                fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                raise CollectorError('Kênh đang được một tác vụ khác xử lý.') from None
        yield
    finally:
        handle.close()  # OS releases lock even if the application was killed.


COLUMNS = ['video_id', 'title', 'url', 'published_at', 'views', 'duration', 'status', 'language', 'transcript_file', 'error']


def safe_csv(value):
    value = str(value)
    return "'" + value if value.lstrip().startswith(('=', '+', '-', '@')) else value


def save_results(folder, rows, state):
    # Manifest committed first: an interruption cannot falsely mark an incomplete file successful.
    atomic_text(folder / 'state.json', json.dumps(state, ensure_ascii=False, indent=2))
    for name, items in [('videos.csv', rows), ('failed_videos.csv', [r for r in rows if r['status'] in ('failed', 'no_transcript')])]:
        out = io.StringIO(newline='')
        writer = csv.DictWriter(out, fieldnames=COLUMNS)
        writer.writeheader()
        for row in items:
            writer.writerow({key: safe_csv(row.get(key, '')) for key in COLUMNS})
        atomic_text(folder / name, out.getvalue(), 'utf-8-sig')


def collect(config, http=None, provider=None, emit=lambda event: None):
    http = http or Http()
    yt = YouTube(config.get('youtube_key', ''), http)
    cid, title, uploads = yt.resolve(config.get('channel_url', ''))
    # ID is stable if channel display name changes and contains no unsafe path components.
    folder = Path(config['output_root']).resolve() / cid
    folder.mkdir(parents=True, exist_ok=True)
    with channel_lock(folder):
        transcripts = folder / 'transcripts'
        transcripts.mkdir(exist_ok=True)
        manifest = folder / 'state.json'
        try:
            state = json.loads(manifest.read_text(encoding='utf-8')) if manifest.exists() else {}
            if not isinstance(state, dict) or any(not isinstance(v, dict) for v in state.values()):
                raise ValueError()
        except (ValueError, UnicodeDecodeError):
            raise CollectorError('state.json hỏng; hãy khôi phục hoặc chọn thư mục khác, không ghi đè lịch sử.') from None
        rows = yt.videos(uploads, lambda message: emit({'type': 'progress', 'message': message}))
        for row in rows:
            vid = row['video_id']
            row.update(status='pending', language='', transcript_file='', error='')
            previous = state.get(vid, {})
            path = transcripts / (vid + '.txt')
            if previous.get('status') == 'success' and path.is_file():
                digest = hashlib.sha256(path.read_bytes()).hexdigest()
                if digest == previous.get('sha256'):
                    row.update(status='success', language=previous.get('language', ''), transcript_file=str(path))
        successful = sum(r['status'] == 'success' for r in rows)
        failed = 0
        save_results(folder, rows, state)
        def report(message, current=''):
            emit(dict(type='progress', channel_id=cid, channel_title=title, folder=str(folder), total=len(rows),
                      success=successful, failed=failed, remaining=len(rows) - successful - failed,
                      current=current, message=message))
        report('Đã lập danh sách, giữ lại transcript thành công từ lần trước.')
        # Credentials/provider only needed for missing transcripts.
        if any(row['status'] == 'pending' for row in rows) and provider is None:
            if config.get('provider') == 'vidiq_mcp':
                from vidiq_provider import VidiqMcp
                provider = VidiqMcp(config.get('mcp_endpoint', ''), config.get('provider_key', ''), http)
            elif config.get('provider', 'supadata') == 'supadata':
                provider = Supadata(config.get('provider_key', ''), http)
            else:
                raise CollectorError('Chọn nguồn transcript hợp lệ trong cấu hình.')
        for row in rows:
            if row['status'] == 'success':
                continue
            vid = row['video_id']
            report('Đang lấy transcript: ' + row['title'], vid)
            try:
                text, language = provider.fetch(vid)
                if not text.strip():
                    raise NoTranscript('Transcript trống.')
                path = transcripts / (vid + '.txt')
                atomic_text(path, text)
                digest = hashlib.sha256(path.read_bytes()).hexdigest()
                row.update(status='success', language=language, transcript_file=str(path), error='')
                state[vid] = dict(status='success', language=language, sha256=digest)
                successful += 1
            except NoTranscript as exc:
                row.update(status='no_transcript', error=str(exc))
                state[vid] = dict(status=row['status'])
                failed += 1
            except CollectorError as exc:
                row.update(status='failed', error=str(exc))
                state[vid] = dict(status=row['status'])
                failed += 1
            except Exception:
                # Do not leak URLs containing credentials, vendor bodies or stack traces.
                row.update(status='failed', error='Lỗi xử lý transcript; kiểm tra cấu hình nguồn và chạy lại.')
                state[vid] = dict(status=row['status'])
                failed += 1
            save_results(folder, rows, state)
            report('Đã lưu kết quả video.', vid)
        report('Hoàn tất thu thập transcript.')
        result = dict(type='complete', folder=str(folder), channel_id=cid, total=len(rows), success=successful, failed=failed, remaining=0,
                      files=[r['transcript_file'] for r in rows if r['status'] == 'success'])
        emit(result)
        return result


def main():
    def emit(event):
        print(json.dumps(event, ensure_ascii=True), flush=True)
    try:
        config = json.load(sys.stdin)
        collect(config, emit=emit)
        return 0
    except CollectorError as exc:
        emit({'type': 'error', 'message': str(exc)})
    except Exception:
        emit({'type': 'error', 'message': 'Không thể thu thập dữ liệu. Kiểm tra URL kênh, cấu hình và quyền ghi thư mục.'})
    return 1


if __name__ == '__main__':
    sys.exit(main())
