import unittest
import tempfile
from datetime import datetime
from pathlib import Path

from lanwatch.discovery import ARP_ROW
from lanwatch.models import Device, Observation
from lanwatch.store import Store


class ArpParsingTests(unittest.TestCase):
    def test_windows_arp_row(self):
        match = ARP_ROW.match("  192.168.1.42          aa-bb-cc-dd-ee-ff     dynamic")
        self.assertIsNotNone(match)
        self.assertEqual("192.168.1.42", match.group("ip"))
        self.assertEqual("aa-bb-cc-dd-ee-ff", match.group("mac"))

    def test_store_round_trip(self):
        with tempfile.TemporaryDirectory() as folder:
            store = Store(Path(folder) / "test.db")
            store.upsert_device(Device("192.168.137.2", "aa:bb:cc:dd:ee:ff"))
            store.add_observation(Observation(
                datetime.now(), "192.168.137.2", "aa:bb:cc:dd:ee:ff", "example.com"
            ))
            self.assertEqual(1, len(store.devices()))
            self.assertEqual("example.com", store.observations()[0]["domain"])
            store.close()


if __name__ == "__main__":
    unittest.main()
