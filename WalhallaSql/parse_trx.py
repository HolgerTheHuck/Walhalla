import xml.etree.ElementTree as ET
from collections import Counter
import re
import sys
import os

# Use UTF-8 for stdout
sys.stdout.reconfigure(encoding='utf-8')

trx_files = sorted([f for f in os.listdir(r'E:/Develop/WalhallaProject/WalhallaSql/TestResults') if f.endswith('.trx')], reverse=True)
trx_path = os.path.join(r'E:/Develop/WalhallaProject/WalhallaSql/TestResults', trx_files[0])
print(f'Parsing TRX: {trx_path}')

tree = ET.parse(trx_path)
root = tree.getroot()

ns = {'ns': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}

results = root.find('ns:Results', ns)
result_summary = root.find('ns:ResultSummary', ns)
counters = result_summary.find('ns:Counters', ns)
total = int(counters.get('total', '0'))
passed = int(counters.get('passed', '0'))
failed = int(counters.get('failed', '0'))
skipped = int(counters.get('skipped', '0'))

print(f'TRX Total: {total}')
print(f'TRX Passed: {passed}')
print(f'TRX Failed: {failed}')
print(f'TRX Skipped: {skipped}')

failures = []
for unit_test_result in results.findall('ns:UnitTestResult', ns):
    outcome = unit_test_result.get('outcome', '')
    if outcome == 'Failed':
        test_name = unit_test_result.get('testName', '')
        output = unit_test_result.find('ns:Output', ns)
        msg_text = None
        if output is not None:
            error_info = output.find('ns:ErrorInfo', ns)
            if error_info is not None:
                message = error_info.find('ns:Message', ns)
                if message is not None and message.text:
                    msg_text = message.text
            if not msg_text:
                stdout_elem = output.find('ns:StdOut', ns)
                if stdout_elem is not None and stdout_elem.text:
                    msg_text = stdout_elem.text[:200]
        if not msg_text:
            msg_text = 'Unknown failure'
        failures.append((test_name, msg_text))

print(f'Extracted {len(failures)} failures from TRX')

def normalize_message(msg):
    # Cluster by first meaningful sentence/phrase
    msg = msg.strip()
    # Remove stack trace
    if '\n' in msg:
        msg = msg.split('\n')[0]
    # Normalize numbers
    msg = re.sub(r'\b\d+\b', '#', msg)
    msg = re.sub(r"'[^']+'", "'...'", msg)
    msg = re.sub(r'"[^"]+"', '"..."', msg)
    # Truncate
    return msg[:120].strip()

clustered = Counter()
for test_name, msg in failures:
    norm = normalize_message(msg)
    clustered[norm] += 1

print('Top 15 failure clusters:')
for msg, count in clustered.most_common(15):
    print(f'{count:4d}: {msg}')

# Also output one representative test per cluster
print('\nRepresentative tests per top cluster:')
seen = set()
for test_name, msg in failures:
    norm = normalize_message(msg)
    if norm not in seen:
        seen.add(norm)
        print(f'{norm}: {test_name}')
    if len(seen) >= 15:
        break
