from pathlib import Path
import unittest
import sys
import json
import hashlib
import struct
import tempfile
import subprocess
import shutil

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from event_codec import decompress, events, patch_flag, wrap
from npc_rules import unlock_tower, simulate_shared, simulate_monk
from reconcile import rebuild, validate

ROOT = Path(__file__).resolve().parents[3]
MOD = ROOT / "SekiroBingo/Resources/me3/sekiro-bingo/mod"


class ImportedAssetsTests(unittest.TestCase):
    def test_dependency_bootstrap_rejects_stale_cached_revisions(self):
        shell = shutil.which('pwsh') or shutil.which('powershell')
        if not shell:
            self.skipTest('PowerShell is required to exercise the dependency bootstrap.')
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            script = root / 'prepare-dependencies.ps1'
            shutil.copyfile(ROOT / 'tools/npc/prepare-dependencies.ps1', script)
            for name in ['soulsformats', 'esdlang']:
                dependency = root / '.deps' / name
                dependency.mkdir(parents=True)
                (dependency / '.revision').write_text('wrong revision')
            infixor = root / '.deps/esdlang/ESDLang/EzSemble/EzInfixor.cs'
            infixor.parent.mkdir(parents=True)
            infixor.write_text('using static SoulsFormats.MSBAC6.Event;')
            (root / '.deps/ActionButtonParam.xml').write_text('stale definition')
            result = subprocess.run([shell, '-NoProfile', '-File', str(script)], capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0, 'Bootstrap silently trusted stale caches.')
            self.assertIn('revision', result.stderr.lower())

    def test_npc_events_and_dialogue_are_shipped(self):
        for relative in [
            "event/common_func.emevd.dcx", "event/m11_01_00_00.emevd.dcx",
            "event/m20_00_00_00.emevd.dcx", "script/talk/m11_01_00_00.talkesdbnd.dcx",
            "script/talk/m20_00_00_00.talkesdbnd.dcx",
        ]:
            with self.subTest(asset=relative):
                self.assertTrue((MOD / relative).is_file(), f"Missing NPC asset: {relative}")

    def test_imported_assets_match_reviewed_manifest(self):
        manifest = json.loads((ROOT / 'tools/npc/assets.json').read_text())
        for relative, info in manifest['files'].items():
            self.assertEqual(hashlib.sha256((MOD / relative).read_bytes()).hexdigest(), info['sha256'], relative)


class CanonicalQuestTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.raw = {name: decompress((MOD / 'event' / name).read_bytes()) for name in
                   ['common_func.emevd.dcx', 'm11_01_00_00.emevd.dcx', 'm20_00_00_00.emevd.dcx']}

    def test_full_progression_matrix(self):
        self.assertEqual(validate(self.raw), {'isshin_cases': 512, 'monk_cases': 1536,
                                              'shrine_cases': 12288, 'chapter_cases': 8})

    def test_tengu_response_and_genichiro_work_in_either_order(self):
        tower = events(self.raw['m11_01_00_00.emevd.dcx'])[11115762]
        for response in [71100500, 71100501]:
            for first, second in [(response, 9303), (9303, response)]:
                flags = {1280: 1, first: 1}
                flags = unlock_tower(tower, flags)
                self.assertFalse(flags.get(1281, 0))
                flags[second] = 1
                self.assertEqual(unlock_tower(tower, flags)[1281], 1)

    def test_early_blade_does_not_move_tengu_or_remove_monk(self):
        common = events(self.raw['common_func.emevd.dcx'])
        flags = {1281: 1, 1660: 1, 1480: 1, 8400: 1, 8410: 1}
        self.assertEqual(simulate_shared(common[20006113], flags)[1660], 1)
        self.assertEqual(simulate_monk(common[20006121], flags)[1480], 1)
        flags[71110206] = 1
        self.assertEqual(simulate_shared(common[20006113], flags)[1661], 1)

    def test_historical_gates_rebuild_to_canonical_without_other_edits(self):
        old = dict(self.raw)
        old['common_func.emevd.dcx'] = patch_flag(old['common_func.emevd.dcx'], 20006121, 10, 11, 9303, 71110206)
        old['common_func.emevd.dcx'] = patch_flag(old['common_func.emevd.dcx'], 20006113, 14, 15, 1281, 71100505)
        old['common_func.emevd.dcx'] = patch_flag(old['common_func.emevd.dcx'], 20006113, 15, 15, 71110206, 8400)
        old['m11_01_00_00.emevd.dcx'] = patch_flag(old['m11_01_00_00.emevd.dcx'], 11115762, 1, 1, 9303, 71110059)
        old['m20_00_00_00.emevd.dcx'] = patch_flag(old['m20_00_00_00.emevd.dcx'], 12005217, 2, 0, 9303, 8400)
        self.assertEqual(rebuild(old), self.raw)
        self.assertEqual(rebuild(self.raw), self.raw)

    def test_wrong_gate_fails_before_output_is_created(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'event').mkdir()
            for name, raw in self.raw.items():
                if name == 'm11_01_00_00.emevd.dcx':
                    raw = patch_flag(raw, 11115762, 1, 1, 9303, 12345)
                (root / 'event' / name).write_bytes(wrap(raw, (MOD / 'event' / name).read_bytes()))
            output = root / 'output'
            result = subprocess.run([sys.executable, str(ROOT / 'tools/npc/reconcile.py'), 'build',
                                     '--mod', str(root), '--output', str(output)], capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('Unknown gate', result.stderr)
            self.assertFalse(output.exists())

    def test_truncated_dcx_is_rejected(self):
        with self.assertRaises(ValueError):
            decompress(b'DCX\0')

    def test_cli_rebuild_ships_valid_dcx_and_preserves_canonical_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'events'
            result = subprocess.run([sys.executable, str(ROOT / 'tools/npc/reconcile.py'), 'build',
                                     '--mod', str(MOD), '--output', str(output)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            for name in self.raw:
                self.assertEqual((output / name).read_bytes(), (MOD / 'event' / name).read_bytes())

    def test_cli_pack_roundtrips_and_refuses_overwrite(self):
        with tempfile.TemporaryDirectory() as directory:
            raw = Path(directory) / 'raw'
            packed = Path(directory) / 'packed.dcx'
            raw.write_bytes(b'BND4' + bytes(300000))
            command = [sys.executable, str(ROOT / 'tools/npc/reconcile.py'), 'pack', '--input', str(raw),
                       '--template', str(MOD / 'event/m11_01_00_00.emevd.dcx'), '--output', str(packed)]
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(decompress(packed.read_bytes()), raw.read_bytes())
            first = packed.read_bytes()
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(packed.read_bytes(), first)


if __name__ == "__main__":
    unittest.main()
