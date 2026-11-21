import json
import os
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from read_reports import execute
print(json.dumps(execute(json.load(sys.stdin),Path(os.environ["TEST_DATA"]))))
