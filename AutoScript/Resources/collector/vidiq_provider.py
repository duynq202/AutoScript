"""Optional MCP adapter. Endpoint and key must be supplied by the user's MCP vendor.
No vidIQ public HTTP API or server URL is assumed. Refuse unknown schemas.
"""
import json
import re
import urllib.parse
from transcript_sources import CollectorError, render


class VidiqMcp:
    def __init__(self, endpoint, key, http):
        url = urllib.parse.urlsplit(endpoint)
        if url.scheme != 'https' or not url.hostname or url.username or url.password or url.query or url.fragment:
            raise CollectorError('Nhập endpoint MCP HTTPS từ tài liệu vidIQ, không nhúng khóa vào URL.')
        self.endpoint, self.http, self.sequence = endpoint, http, 0
        self.headers = {'Authorization': 'Bearer ' + key} if key else {}
        result = self.rpc('initialize', {'protocolVersion': '2025-03-26', 'capabilities': {}, 'clientInfo': {'name': 'AutoScript', 'version': '1.2.0'}})
        version = result.get('protocolVersion')
        if version not in ('2024-11-05', '2025-03-26', '2025-06-18', '2025-11-25'):
            raise CollectorError('Phiên bản MCP chưa hỗ trợ.')
        self.headers['MCP-Protocol-Version'] = version
        self.http.request('POST', self.endpoint, self.headers, {'jsonrpc': '2.0', 'method': 'notifications/initialized'})
        tools, cursor, seen = [], None, set()
        while True:
            page = self.rpc('tools/list', {'cursor': cursor} if cursor else {})
            tools.extend(page.get('tools', []))
            cursor = page.get('nextCursor')
            if not cursor:
                break
            if cursor in seen:
                raise CollectorError('MCP lặp cursor tools/list.')
            seen.add(cursor)
        matches = [x for x in tools if x.get('name') in ('vidiq_video_transcript', 'video_transcript', 'get_video_transcript')]
        if len(matches) != 1:
            raise CollectorError('Endpoint không cung cấp vidiq_video_transcript. Kiểm tra tài liệu và endpoint thật.')
        self.tool_name = matches[0]['name']
        schema = matches[0].get('inputSchema', {})
        props = schema.get('properties', {})
        candidates = [p for p in ('video_id', 'videoId', 'video_url', 'url') if p in props and props[p].get('type') == 'string']
        if len(candidates) != 1 or set(schema.get('required', [])) - set(candidates):
            raise CollectorError('Schema tool vidIQ chưa được hỗ trợ; cần tài liệu output/input thật để bổ sung adapter.')
        self.argument = candidates[0]

    def rpc(self, method, params):
        self.sequence += 1
        value, headers = self.http.request('POST', self.endpoint, self.headers,
            {'jsonrpc': '2.0', 'id': self.sequence, 'method': method, 'params': params})
        for name, text in headers.items():
            if name.lower() == 'mcp-session-id':
                self.headers['Mcp-Session-Id'] = text
        if not isinstance(value, dict) or value.get('id') != self.sequence or 'error' in value or not isinstance(value.get('result'), dict):
            raise CollectorError('MCP từ chối yêu cầu hoặc trả dữ liệu không hợp lệ. Kiểm tra quyền và tài liệu server.')
        return value['result']

    def fetch(self, vid):
        argument = 'https://www.youtube.com/watch?v=' + vid if self.argument in ('video_url', 'url') else vid
        value = self.rpc('tools/call', {'name': self.tool_name, 'arguments': {self.argument: argument}})
        if value.get('isError'):
            raise CollectorError('Tool vidIQ báo lỗi khi lấy transcript.')
        structured = value.get('structuredContent')
        if isinstance(structured, dict):
            segments = structured.get('segments', structured.get('transcript'))
            if isinstance(segments, list):
                return render(segments), structured.get('language', '')
        for block in value.get('content', []):
            if block.get('type') != 'text':
                continue
            text = block.get('text', '')
            try:
                data = json.loads(text)
                if isinstance(data, dict):
                    segments = data.get('segments', data.get('transcript'))
                    if isinstance(segments, list):
                        return render(segments), data.get('language', '')
            except (ValueError, TypeError):
                pass
            if isinstance(text, str) and text.strip() and all(re.match(r'^\[\d{2,}:\d{2}:\d{2}(?:\.\d{3})?\] ', line) for line in text.splitlines() if line.strip()):
                return text, ''
        raise CollectorError('Output vidIQ thiếu timestamp hoặc schema chưa hỗ trợ; cần adapter theo tài liệu server thật.')
