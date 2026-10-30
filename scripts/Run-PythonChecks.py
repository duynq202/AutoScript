"""Expose the same test output in CI summary, preserving the real test exit status."""
import os
import subprocess
import sys
from pathlib import Path

result = subprocess.run([sys.executable, '-m', 'unittest', 'discover', '-s', 'tests', '-v'],
                        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, encoding='utf-8', errors='replace',
                        env={**os.environ, 'PYTHONIOENCODING': 'utf-8'})
print(result.stdout)
summary = os.environ.get('GITHUB_STEP_SUMMARY')
if summary:
    with Path(summary).open('a', encoding='utf-8') as stream:
        stream.write('## Python checks\n\n```text\n' + result.stdout.replace('```', '') + '\n```\n')
sys.exit(result.returncode)
