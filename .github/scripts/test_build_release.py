#!/usr/bin/env python3
"""Regression tests for deterministic release archive construction."""

from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("build_release.py")
SPEC = importlib.util.spec_from_file_location("build_release", SCRIPT)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Could not load {SCRIPT}")
BUILD_RELEASE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = BUILD_RELEASE
SPEC.loader.exec_module(BUILD_RELEASE)


class ReleaseArchiveTests(unittest.TestCase):
    def test_upm_archive_normalizes_txt_line_endings(self) -> None:
        original_package = BUILD_RELEASE.PACKAGE
        try:
            with tempfile.TemporaryDirectory() as temporary_directory:
                root = Path(temporary_directory)
                package = root / "package-source"
                package.mkdir()
                text_file = package / "PublicApiSurface.txt"
                BUILD_RELEASE.PACKAGE = package

                text_file.write_bytes(b"first\r\nsecond\r\n")
                windows_archive = root / "windows.tgz"
                BUILD_RELEASE.build_upm_archive(windows_archive)

                text_file.write_bytes(b"first\nsecond\n")
                linux_archive = root / "linux.tgz"
                BUILD_RELEASE.build_upm_archive(linux_archive)

                self.assertEqual(windows_archive.read_bytes(), linux_archive.read_bytes())
        finally:
            BUILD_RELEASE.PACKAGE = original_package


if __name__ == "__main__":
    unittest.main()
