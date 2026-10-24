"""Scrapling crawler package.

Round 19: the worker is split into sibling modules. Adding this directory to
``sys.path`` keeps the historical top-level imports (``from models import ...``,
``from robots import ...``) working both when ``scrapling_worker.py`` is run as
a script and when the package is imported by the unit tests.
"""
import os
import sys

_THIS_DIR = os.path.dirname(os.path.abspath(__file__))
if _THIS_DIR not in sys.path:
    sys.path.insert(0, _THIS_DIR)
