import csv
import os
import queue
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

from .capture import DnsCapture
from .discovery import discover_arp_devices
from .models import Device
from .store import Store


class LanWatchApp:
    def __init__(self, root):
        self.root = root
        self.root.title("LAN URL Watch")
        self.root.geometry("1100x680")
        data_dir = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "LanUrlWatch"
        self.store = Store(data_dir / "lanwatch.db")
        self.events = queue.Queue()
        self.selected_ip = ""
        self.status = tk.StringVar(value="Starting…")
        self.search = tk.StringVar()
        self.interface = tk.StringVar(value="Default adapter")
        self.capture = DnsCapture(
            lambda item: self.events.put(("observation", item)),
            lambda text: self.events.put(("status", text)),
        )
        self._build()
        self.root.protocol("WM_DELETE_WINDOW", self.close)
        self.root.after(100, self._process_events)
        self.root.after(250, self._refresh_discovery)
        self.capture.start(self._selected_interface())

    def _build(self):
        bar = ttk.Frame(self.root, padding=8)
        bar.pack(fill="x")
        ttk.Button(bar, text="Pause / Resume", command=self._toggle).pack(side="left")
        ttk.Button(bar, text="Export CSV", command=self._export).pack(side="left", padx=6)
        ttk.Label(bar, text="LAN adapter:").pack(side="left", padx=(12, 5))
        self.interfaces = ttk.Combobox(bar, textvariable=self.interface, state="readonly", width=24)
        self.interfaces["values"] = self._capture_interfaces()
        self.interfaces.pack(side="left")
        self.interfaces.bind("<<ComboboxSelected>>", lambda _e: self._restart_capture())
        ttk.Label(bar, text="Search domains:").pack(side="left", padx=(18, 5))
        entry = ttk.Entry(bar, textvariable=self.search, width=32)
        entry.pack(side="left")
        entry.bind("<KeyRelease>", lambda _e: self._refresh_views())

        panes = ttk.Panedwindow(self.root, orient="horizontal")
        panes.pack(fill="both", expand=True, padx=8, pady=(0, 8))
        left, right = ttk.Frame(panes), ttk.Frame(panes)
        panes.add(left, weight=1); panes.add(right, weight=3)

        ttk.Label(left, text="Devices", font=("Segoe UI", 12, "bold")).pack(anchor="w")
        self.devices = ttk.Treeview(left, columns=("ip", "mac", "requests"), show="headings")
        for key, label, width in (("ip", "IP address", 120), ("mac", "MAC", 135),
                                  ("requests", "Requests", 70)):
            self.devices.heading(key, text=label); self.devices.column(key, width=width)
        self.devices.pack(fill="both", expand=True)
        self.devices.bind("<<TreeviewSelect>>", self._select_device)

        ttk.Label(right, text="Observed domains (HTTPS paths stay encrypted)",
                  font=("Segoe UI", 12, "bold")).pack(anchor="w")
        self.activity = ttk.Treeview(
            right, columns=("time", "ip", "domain", "detail", "type"), show="headings"
        )
        for key, label, width in (("time", "Time", 155), ("ip", "Device IP", 115),
                                  ("domain", "Domain", 220), ("detail", "Available URL detail", 300),
                                  ("type", "Source", 70)):
            self.activity.heading(key, text=label); self.activity.column(key, width=width)
        self.activity.pack(fill="both", expand=True)

        ttk.Label(self.root, textvariable=self.status, relief="sunken", anchor="w",
                  padding=5).pack(fill="x")

    def _process_events(self):
        changed = False
        while True:
            try: kind, value = self.events.get_nowait()
            except queue.Empty: break
            if kind == "observation":
                self.store.upsert_device(Device(value.source_ip, value.source_mac))
                self.store.add_observation(value); changed = True
            else: self.status.set(value)
        if changed: self._refresh_views()
        self.root.after(250, self._process_events)

    def _refresh_discovery(self):
        def work():
            try:
                for device in discover_arp_devices(): self.store.upsert_device(device)
                self.events.put(("refresh", None))
            except Exception as exc: self.events.put(("status", f"Discovery warning: {exc}"))
        threading.Thread(target=work, daemon=True).start()
        self._refresh_views()
        self.root.after(15000, self._refresh_discovery)

    def _refresh_views(self):
        selected = self.selected_ip
        self.devices.delete(*self.devices.get_children())
        for row in self.store.devices():
            iid = self.devices.insert("", "end", values=(row["ip"], row["mac"], row["requests"]))
            if row["ip"] == selected: self.devices.selection_set(iid)
        self.activity.delete(*self.activity.get_children())
        for row in self.store.observations(selected, self.search.get().strip()):
            time = row["observed_at"].replace("T", " ")[:19]
            self.activity.insert("", "end", values=(time, row["source_ip"], row["domain"],
                                                      row["url_detail"], row["query_type"]))

    def _select_device(self, _event):
        selection = self.devices.selection()
        self.selected_ip = self.devices.item(selection[0], "values")[0] if selection else ""
        self._refresh_views()

    def _toggle(self):
        self.capture.stop() if self.capture.running else self.capture.start(self._selected_interface())

    def _capture_interfaces(self):
        try:
            from scapy.arch.windows import get_windows_if_list
            names = [item.get("name") for item in get_windows_if_list() if item.get("name")]
            return ["Default adapter", *sorted(set(names))]
        except Exception:
            return ["Default adapter"]

    def _selected_interface(self):
        value = self.interface.get()
        return None if value == "Default adapter" else value

    def _restart_capture(self):
        if self.capture.running:
            self.capture.stop()
            self.capture.start(self._selected_interface())

    def _export(self):
        path = filedialog.asksaveasfilename(defaultextension=".csv", filetypes=[("CSV", "*.csv")])
        if not path: return
        rows = self.store.observations(self.selected_ip, self.search.get().strip(), 1000000)
        with open(path, "w", newline="", encoding="utf-8-sig") as output:
            writer = csv.writer(output); writer.writerow(("time", "device_ip", "mac", "domain", "url_detail", "source"))
            writer.writerows((r["observed_at"], r["source_ip"], r["source_mac"], r["domain"],
                              r["url_detail"], r["query_type"]) for r in rows)
        messagebox.showinfo("LAN URL Watch", f"Exported {len(rows)} rows.")

    def close(self):
        self.capture.stop(); self.store.close(); self.root.destroy()


def main():
    root = tk.Tk()
    LanWatchApp(root)
    root.mainloop()
