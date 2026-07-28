import unittest
import tempfile
from datetime import datetime
from pathlib import Path

from lanwatch.discovery import ARP_ROW
from lanwatch.capture import extract_http_request, extract_tls_sni
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

    def test_extracts_plaintext_http_url(self):
        result = extract_http_request(
            b"GET /watch?v=123 HTTP/1.1\r\nHost: video.example.com\r\n\r\n"
        )
        self.assertEqual(("video.example.com", "http://video.example.com/watch?v=123"), result)

    def test_non_tls_payload_has_no_sni(self):
        self.assertEqual("", extract_tls_sni(b"not a tls client hello"))


if __name__ == "__main__":
    unittest.main()
