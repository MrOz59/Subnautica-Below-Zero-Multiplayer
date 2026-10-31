import sys
import tempfile
import unittest
from unittest import mock
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import launcher


class LauncherTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def game(self):
        path = self.root / "steamapps" / "common" / "SubnauticaZero"
        path.mkdir(parents=True, exist_ok=True)
        (path / "SubnauticaZero.exe").write_bytes(b"game-placeholder")
        return path

    def loader(self, path):
        core = path / "BepInEx" / "core"
        core.mkdir(parents=True, exist_ok=True)
        (core / "BepInEx.dll").write_bytes(b"loader-placeholder")
        (path / "winhttp.dll").write_bytes(b"proxy-placeholder")

    def test_install_discovery_from_manifest(self):
        game = self.game()
        (self.root / "steamapps" / "appmanifest_848450.acf").write_text(
            '"AppState" { "appid" "848450" "installdir" "SubnauticaZero" }',
            encoding="utf-8",
        )
        self.assertEqual(launcher.detect_installations([self.root]), [game])

    def test_multiple_steam_libraries(self):
        second = self.root / "library two"
        second.mkdir()
        (self.root / "steamapps" ).mkdir(exist_ok=True)
        (self.root / "steamapps" / "libraryfolders.vdf").write_text(
            '"libraryfolders" { "1" { "path" "' + str(second) + '" } }',
            encoding="utf-8",
        )
        libraries = launcher.steam_libraries([self.root])
        self.assertIn(second, libraries)

    def test_diagnosis_requires_loader(self):
        game = self.game()
        self.assertFalse(launcher.doctor(game)[1])
        self.loader(game)
        self.assertFalse(launcher.doctor(game)[1])

    def test_installer_does_not_replace_unrelated_files(self):
        game = self.game()
        self.loader(game)
        builds = self.root / "build"
        builds.mkdir()
        plugin = builds / launcher.PLUGIN_NAME
        protocol = builds / launcher.PROTOCOL_NAME
        plugin.write_bytes(b"our-plugin-v1")
        protocol.write_bytes(b"our-protocol-v1")
        launcher.install_plugin(game, plugin, protocol)
        target = game / "BepInEx" / "plugins" / "BelowZeroCoop"
        self.assertEqual((target / launcher.PLUGIN_NAME).read_bytes(), b"our-plugin-v1")
        self.assertEqual((game / "winhttp.dll").read_bytes(), b"proxy-placeholder")

        plugin.write_bytes(b"our-plugin-v2")
        with self.assertRaises(FileExistsError):
            launcher.install_plugin(game, plugin, protocol)
        self.assertEqual((target / launcher.PLUGIN_NAME).read_bytes(), b"our-plugin-v1")

        launcher.install_plugin(game, plugin, protocol, force=True)
        self.assertEqual((target / launcher.PLUGIN_NAME).read_bytes(), b"our-plugin-v2")
        self.assertTrue(list(target.glob(launcher.PLUGIN_NAME + ".*.bak")))

    def test_proton_options(self):
        self.assertIn("WINEDLLOVERRIDES", launcher.STEAM_PROTON_OPTIONS)
        self.assertIn("%command%", launcher.STEAM_PROTON_OPTIONS)


    def test_conflict_on_second_assembly_leaves_first_unchanged(self):
        game = self.game()
        self.loader(game)
        output = game / "BepInEx" / "plugins" / "BelowZeroCoop"
        output.mkdir(parents=True, exist_ok=True)
        (output / launcher.PROTOCOL_NAME).write_bytes(b"old-protocol")

        source = self.root / "build"
        source.mkdir()
        plugin = source / launcher.PLUGIN_NAME
        protocol = source / launcher.PROTOCOL_NAME
        plugin.write_bytes(b"new-plugin")
        protocol.write_bytes(b"new-protocol")

        with self.assertRaises(FileExistsError):
            launcher.install_plugin(game, plugin, protocol)
        self.assertFalse((output / launcher.PLUGIN_NAME).exists())
        self.assertEqual((output / launcher.PROTOCOL_NAME).read_bytes(), b"old-protocol")

    def test_installer_restores_existing_files_after_commit_failure(self):
        game = self.game()
        self.loader(game)
        output = game / "BepInEx" / "plugins" / "BelowZeroCoop"
        output.mkdir(parents=True, exist_ok=True)
        (output / launcher.PLUGIN_NAME).write_bytes(b"old-plugin")
        (output / launcher.PROTOCOL_NAME).write_bytes(b"old-protocol")

        source = self.root / "build"
        source.mkdir()
        plugin = source / launcher.PLUGIN_NAME
        protocol = source / launcher.PROTOCOL_NAME
        plugin.write_bytes(b"new-plugin")
        protocol.write_bytes(b"new-protocol")

        original_replace = launcher.os.replace
        calls = 0

        def fail_second_install(source_path, dest_path):
            nonlocal calls
            calls += 1
            if calls == 2:
                raise OSError("simulated installation failure")
            return original_replace(source_path, dest_path)

        with mock.patch.object(launcher.os, "replace", side_effect=fail_second_install):
            with self.assertRaises(OSError):
                launcher.install_plugin(game, plugin, protocol, force=True)

        self.assertEqual((output / launcher.PLUGIN_NAME).read_bytes(), b"old-plugin")
        self.assertEqual((output / launcher.PROTOCOL_NAME).read_bytes(), b"old-protocol")
        self.assertFalse(list(output.glob(".bzc-*.tmp")))

    def test_idempotent_install_has_no_backups(self):
        game = self.game()
        self.loader(game)
        source = self.root / "build"
        source.mkdir()
        plugin = source / launcher.PLUGIN_NAME
        protocol = source / launcher.PROTOCOL_NAME
        plugin.write_bytes(b"plugin")
        protocol.write_bytes(b"protocol")
        launcher.install_plugin(game, plugin, protocol)
        log = launcher.install_plugin(game, plugin, protocol)
        self.assertEqual(len(log), 2)
        self.assertTrue(all("Unchanged:" in message for message in log))
        folder = game / "BepInEx" / "plugins" / "BelowZeroCoop"
        self.assertFalse(list(folder.glob("*.bak")))


if __name__ == "__main__":
    unittest.main()
