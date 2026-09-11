#!/usr/bin/env python3
"""Run all repository tooling tests with the standard library test runner."""

from __future__ import annotations

import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def main() -> int:
    suite = unittest.defaultTestLoader.discover(
        str(ROOT / "tools" / "tests"), pattern="test_*.py"
    )
    if suite.countTestCases() == 0:
        print("No tooling tests discovered.", file=sys.stderr)
        return 1
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main())
