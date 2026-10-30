"""Counts use Python str.split and len, matching the supplied rewrite skill."""
import json
import re
import sys
from pathlib import Path


def analyze(text):
    text = text.replace('\r\n', '\n').replace('\r', '\n').strip('\n')
    lines = text.split('\n') if text else []
    errors = []
    if not lines:
        errors.append('Kịch bản trống')
    if any(not line.strip() for line in lines):
        errors.append('Có dòng trắng')
    if any(line != line.strip() for line in lines):
        errors.append('Có khoảng trắng thừa đầu hoặc cuối dòng')
    if re.search(r'\d', text):
        errors.append('Có chữ số')
    if re.search(r"[^A-Za-z \n.,?!\"'\-]", text):
        errors.append('Có ký tự cấm')
    if re.search(r"\b\w+'(?:t|d|ll|re|ve|m)\b|\b(?:let's|it's|that's|there's|here's|what's|who's|where's|when's|how's)\b", text, re.I):
        errors.append('Có từ rút gọn')
    if re.search(r'\b(?:Mr|Mrs|Ms|Dr|St|Jr|Sr|TV|OK|etc|vs)\b\.?', text):
        errors.append('Có từ viết tắt')
    if re.search(r'\.{3}|(?<![A-Za-z])-(?![A-Za-z])', text):
        errors.append('Có dấu ba chấm hoặc gạch nối đứng riêng')
    long_lines = [i + 1 for i, line in enumerate(lines) if len(line.split()) > 110]
    if long_lines:
        errors.append('Đoạn quá một trăm mười từ: ' + ', '.join(map(str, long_lines)))
    return dict(words=len(text.split()), characters=len(text), paragraphs=len(lines), errors=errors)


if __name__ == '__main__':
    text = Path(sys.argv[1]).read_text(encoding='utf-8-sig')
    result = analyze(text)
    if '--count-only' in sys.argv:
        result['words'] = len(text.split())
        result['characters'] = len(text)
        result['errors'] = []
    print(json.dumps(result, ensure_ascii=True))
