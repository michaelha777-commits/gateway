"""Legacy DSM omits EnvironmentFiles entirely, even with systemctl show --all."""
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch
import subprocess
from nas_unit import unit_property, verify_environment_files


class LegacyUnitTests(unittest.TestCase):
    def setUp(self):
        scratch=tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.root=Path(scratch.name)
        self.fragment=self.root/"homewatch3.service"
        self.dropin=self.root/"10-synology.conf"
        self.env=self.root/"private settings"
        self.env.write_text("TOKEN=fixture-secret")
        self.fragment.write_text("[Service]\nEnvironmentFile=/replaced-by-dropin\n")
        self.dropin.write_text('[Service]\nEnvironmentFile=\nEnvironmentFile="'+str(self.env)+'"\nEnvironmentFile=-'+str(self.root/"optional")+'\n')
        self.definitions=[self.fragment,self.dropin]

    def test_legacy_systemctl(self):
        shim=self.root/"systemctl"
        shim.write_text('#!/bin/sh\n[ "$*" = "show --all -p EnvironmentFiles homewatch3.service" ] || exit 3\nexit 0\n')
        shim.chmod(0o755)
        with patch.dict(os.environ,PATH=str(self.root)+os.pathsep+os.environ["PATH"]):
            reported=unit_property("EnvironmentFiles")
            self.assertIsNone(reported)
            with self.assertRaisesRegex(ValueError,"Cannot query"): unit_property("ExecStart")
        verify_environment_files(reported,self.definitions,time.time()+1)
        with self.assertRaisesRegex(ValueError,"changed"): verify_environment_files(reported,self.definitions,1)
        self.env.unlink()
        with self.assertRaisesRegex(ValueError,"missing"): verify_environment_files(reported,self.definitions,time.time()+1)

    def test_no_directive_and_unsupported_syntax(self):
        self.fragment.write_text("[Service]\nEnvironment=TOKEN=fixture-secret\n")
        verify_environment_files(None,[self.fragment],time.time()+1)
        for directive in ["EnvironmentFile=/etc/%i","EnvironmentFile=/etc/*.env",".include /etc/other","EnvironmentFile=/etc/a"+chr(92)]:
            self.dropin.write_text("[Service]\n"+directive)
            with self.assertRaises(ValueError): verify_environment_files(None,self.definitions,time.time()+1)

    def test_property_error_must_identify_an_unsupported_property(self):
        for message in ("Unknown property EnvironmentFiles.","Access denied"):
            error=subprocess.CalledProcessError(1,"systemctl",stderr=message)
            with patch("nas_unit.subprocess.check_output",side_effect=error):
                if message.startswith("Unknown"): self.assertIsNone(unit_property("EnvironmentFiles"))
                else:
                    with self.assertRaisesRegex(ValueError,"Cannot query"): unit_property("EnvironmentFiles")

    def test_modern_property_does_not_parse_unit_contents(self):
        verify_environment_files('"'+str(self.env)+'" (ignore_errors=no)',[],time.time()+1)
        with patch("nas_unit.subprocess.check_output",return_value="ExecStart=wrong\n"):
            with self.assertRaises(ValueError): unit_property("EnvironmentFiles")


if __name__=="__main__": unittest.main()
