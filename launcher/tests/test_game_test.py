"""Regression tests for preparation of a safe, local Below Zero game test."""
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import launcher
import game_test


class SoloTestTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.game = self.root / "SubnauticaZero"
        self.game.mkdir()
        (self.game / "SubnauticaZero.exe").write_bytes(b"fake-game")
        core = self.game / "BepInEx" / "core"
        core.mkdir(parents=True)
        (core / "BepInEx.dll").write_bytes(b"fake-loader")
        (core / "0Harmony.dll").write_bytes(b"fake-harmony")
        (self.game / "winhttp.dll").write_bytes(b"fake-proxy")
        managed = self.game / "SubnauticaZero_Data" / "Managed"
        managed.mkdir(parents=True)
        (managed / "Assembly-CSharp.dll").write_bytes(b"fake-game-api")
        (managed / "UnityEngine.CoreModule.dll").write_bytes(b"fake-unity-api")
        self.dlls = self.root / "compiled"
        self.dlls.mkdir()
        (self.dlls / launcher.PLUGIN_NAME).write_bytes(b"our-plugin")
        (self.dlls / launcher.PROTOCOL_NAME).write_bytes(b"our-protocol")

    def test_build_uses_local_game_references_without_shell(self):
        fake_dotnet = self.root / "dotnet"
        fake_dotnet.write_text("fake")
        with mock.patch.object(game_test.shutil, "which",
                               return_value=str(fake_dotnet)), \
             mock.patch.object(game_test.subprocess, "run") as run, \
             mock.patch.object(game_test.Path, "is_file",
                               autospec=True, side_effect=lambda p: True):
            plugin, protocol = game_test.build_game_plugin(self.game, self.root)
        args = run.call_args.args[0]
        self.assertEqual(args[:3], [str(fake_dotnet), "build",
                                  str(self.root / "src" / "BelowZeroCoop.Plugin" /
                                      "BelowZeroCoop.Plugin.csproj")])
        self.assertIn("-p:BepInExCore=" + str(self.game / "BepInEx" / "core"), args)
        self.assertIn("-p:GameManaged=" +
                      str(self.game / "SubnauticaZero_Data" / "Managed"), args)
        self.assertTrue(run.call_args.kwargs["check"])
        self.assertNotIn("shell", run.call_args.kwargs)
        self.assertEqual(plugin.name, launcher.PLUGIN_NAME)
        self.assertEqual(protocol.name, launcher.PROTOCOL_NAME)

    def test_missing_real_managed_files_blocks_build(self):
        (self.game / "SubnauticaZero_Data" / "Managed" / "Assembly-CSharp.dll").unlink()
        with mock.patch.object(game_test.subprocess, "run") as run:
            with self.assertRaisesRegex(FileNotFoundError, "Assembly-CSharp"):
                game_test.build_game_plugin(self.game, self.root)
        run.assert_not_called()

    def test_prepare_safe_mode_disables_network_and_preserves_unrelated_config(self):
        path = self.game / "BepInEx" / "config" / game_test.CONFIG_NAME
        path.parent.mkdir()
        original = (
            "# user-owned comments\n"
            "[Network]\n"
            "EnableProbe = true\n"
            "Host = 10.1.2.3\n"
            "Port = 27021\n"
            "[Development]\n"
            "EnableMovementPreview = true\n"
            "ObserveBuildingHooks = true\n"
            "[OtherMod]\n"
            "Keep = 42\n"
        )
        path.write_text(original)
        with mock.patch.object(launcher, "DEFAULT_PLUGIN",
                               self.dlls / launcher.PLUGIN_NAME), \
             mock.patch.object(launcher, "DEFAULT_PROTOCOL",
                               self.dlls / launcher.PROTOCOL_NAME):
            messages = launcher.prepare_game_test(self.game, build=False)
        text = path.read_text()
        self.assertIn("Host = 10.1.2.3", text)
        self.assertIn("[OtherMod]\nKeep = 42", text)
        self.assertIn("ScanGameHooksOnStartup = true", text)
        self.assertIn("EnableProbe = false", text)
        self.assertIn("EnableMovementPreview = false", text)
        self.assertIn("ObserveBuildingHooks = false", text)
        self.assertIn("ObserveDeconstructionHooks = false", text)
        self.assertIn("READY FOR SOLO GAME TEST", "\n".join(messages))
        backups = list(path.parent.glob(path.name + ".*.bak"))
        self.assertEqual(len(backups), 1)
        self.assertEqual(backups[0].read_text(), original)
        self.assertEqual(len(launcher.install_plugin(
            self.game, self.dlls / launcher.PLUGIN_NAME,
            self.dlls / launcher.PROTOCOL_NAME)), 2)

        # Repeated preparation is idempotent and does not overwrite backup.
        with mock.patch.object(launcher, "DEFAULT_PLUGIN",
                               self.dlls / launcher.PLUGIN_NAME), \
             mock.patch.object(launcher, "DEFAULT_PROTOCOL",
                               self.dlls / launcher.PROTOCOL_NAME):
            launcher.prepare_game_test(self.game, build=False)
        self.assertEqual(len(list(path.parent.glob(path.name + ".*.bak"))), 1)

    def test_no_prebuilt_dlls_cannot_modify_configuration(self):
        existing = self.game / "BepInEx" / "config" / game_test.CONFIG_NAME
        existing.parent.mkdir()
        existing.write_text("[Network]\nEnableProbe = true\n")
        with mock.patch.object(launcher, "DEFAULT_PLUGIN",
                               self.root / "missing-plugin.dll"), \
             mock.patch.object(launcher, "DEFAULT_PROTOCOL",
                               self.root / "missing-protocol.dll"):
            with self.assertRaises(FileNotFoundError):
                launcher.prepare_game_test(self.game, build=False)
        self.assertEqual(existing.read_text(), "[Network]\nEnableProbe = true\n")
        self.assertFalse(list(existing.parent.glob("*.bak")))

    def test_network_is_disabled_before_plugin_install(self):
        config = self.game / "BepInEx" / "config" / game_test.CONFIG_NAME
        config.parent.mkdir()
        config.write_text("[Network]\nEnableProbe = true\n")
        with mock.patch.object(launcher, "DEFAULT_PLUGIN",
                               self.dlls / launcher.PLUGIN_NAME), \
             mock.patch.object(launcher, "DEFAULT_PROTOCOL",
                               self.dlls / launcher.PROTOCOL_NAME), \
             mock.patch.object(launcher, "install_plugin",
                               side_effect=OSError("simulated installer error")):
            with self.assertRaisesRegex(OSError, "installer error"):
                launcher.prepare_game_test(self.game, build=False)
        self.assertIn("EnableProbe = false", config.read_text())
        self.assertEqual(len(list(config.parent.glob("*.bak"))), 1)

    def test_optional_hooks_are_explicit(self):
        game_test.configure_solo_test(
            self.game, observe_builds=True, observe_deconstruction=True)
        cfg = (self.game / "BepInEx" / "config" /
               game_test.CONFIG_NAME).read_text()
        self.assertIn("ObserveBuildingHooks = true", cfg)
        self.assertIn("ObserveDeconstructionHooks = true", cfg)
        self.assertIn("EnableProbe = false", cfg)
        self.assertIn("EnableMovementPreview = false", cfg)

    def test_duplicate_owned_keys_are_sanitized(self):
        cfg = "[Network]\nEnableProbe = true\nEnableProbe = true\n"
        parsed = game_test._update_ini(cfg, {
            "Network": {"EnableProbe": "false"},
        })
        self.assertEqual(parsed.count("EnableProbe = false"), 1)
        self.assertNotIn("EnableProbe = true", parsed)

    def test_report_uses_existing_bepinex_log(self):
        log = self.game / "BepInEx" / "LogOutput.log"
        log.write_text(
            "[Info] unrelated\n"
            "[Info] BelowZeroCoop loaded. No playable multiplayer features yet.\n"
            "[Info] [HookScan] Found Constructable.Construct\n"
            "[Info] [GameTest] Player.main available\n"
        )
        report = game_test.game_test_report(self.game)
        self.assertEqual(len(report), 5)
        self.assertNotIn("unrelated", "\n".join(report))
        self.assertEqual(len(game_test.game_test_report(self.game, 1)), 3)
        with self.assertRaises(ValueError):
            game_test.game_test_report(self.game, 0)

    def test_no_game_directory_or_no_windows_binary_fails_closed(self):
        with self.assertRaises(ValueError):
            launcher.prepare_game_test(self.root, build=False)
        (self.game / "SubnauticaZero.exe").unlink()
        # Keep an otherwise valid native-only installation to exercise
        # the dedicated Windows/Proton compatibility guard.
        (self.game / "SubnauticaZero.x86_64").write_bytes(b"native-linux-binary")
        with self.assertRaisesRegex(ValueError, "Windows"):
            launcher.prepare_game_test(self.game, build=False)


if __name__ == "__main__":
    unittest.main()
