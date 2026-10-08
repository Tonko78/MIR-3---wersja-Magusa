from pathlib import Path
import re
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "ops" / "mir3-web" / "Mir3-Launcher.iss"


class InstallerContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.text = SCRIPT.read_text(encoding="utf-8")
        cls.sections = {}
        current = None
        for line in cls.text.splitlines():
            section = re.fullmatch(r"\[([A-Za-z]+)\]", line.strip())
            if section:
                current = section.group(1).lower()
                cls.sections[current] = []
            elif current and line.strip() and not line.lstrip().startswith(";"):
                cls.sections[current].append(line.strip())

    def test_per_user_writable_install_without_elevation(self):
        setup = "\n".join(self.sections["setup"]).lower()
        self.assertRegex(setup, r"(?m)^privilegesrequired\s*=\s*lowest$")
        self.assertRegex(setup, r"(?m)^defaultdirname\s*=\s*\{localappdata\}\\programs\\mir3 zircon$")
        self.assertRegex(setup, r"(?m)^appname\s*=\s*mir3 zircon$")

    def test_only_two_bootstrap_executables_are_bundled(self):
        files = "\n".join(self.sections["files"]).lower()
        self.assertEqual(len(self.sections["files"]), 2)
        self.assertIn("launcher.exe", files)
        self.assertIn("patcher.exe", files)
        for unwanted in ("zircon.exe", "system.db", "users.db", ".gz", ".7z", ".zip"):
            self.assertNotIn(unwanted, files)
        self.assertNotIn("ftp://", self.text.lower())

    def test_start_menu_optional_desktop_and_post_install_run(self):
        icons = "\n".join(self.sections["icons"]).lower()
        tasks = "\n".join(self.sections["tasks"]).lower()
        run = "\n".join(self.sections["run"]).lower()
        self.assertIn("{group}", icons)
        self.assertIn("{autodesktop}", icons)
        self.assertIn("desktopicon", tasks)
        self.assertIn("launcher.exe", run)
        self.assertIn("postinstall", run)
        self.assertIn("skipifsilent", run)


if __name__ == "__main__":
    unittest.main()
