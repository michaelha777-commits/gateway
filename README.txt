HOMEWATCH FOR ADGUARD HOME
==========================

1. Extract the HomeWatch folder to C:\HomeWatch.
2. Double-click Start-HomeWatch.cmd.
3. Your browser opens at http://127.0.0.1:8765.
4. Enter:
     AdGuard address: http://127.0.0.1
     Your existing AdGuard Home username and password
5. To start HomeWatch automatically, double-click Install-Startup.cmd
   and approve the Administrator prompt.

HomeWatch reads AdGuard Home's local query log and stores its own data in:
  C:\Users\<your-user>\AppData\Local\HomeWatch

Your AdGuard password is protected using Windows DPAPI and can only be
decrypted by the same Windows user account on this computer.

VIRUSTOTAL DOMAIN DETAILS
-------------------------
In Data settings, paste your personal VirusTotal API key and save. HomeWatch
encrypts the key with Windows DPAPI for the current user; it never returns the
saved key to the browser. Explain domain then shows VirusTotal categories,
reputation, and security-engine counts inside HomeWatch, together with public
urlscan.io observation details. Only the domain being explained is sent to
these services. Leave the key box empty when saving other settings to keep the
existing key, or select Remove the saved VirusTotal key to delete it.

PHONE ALERTS AND ACKNOWLEDGEMENTS
--------------------------------
Install the ntfy app on the phone and subscribe to a long, private topic name.
In HomeWatch Data and notification settings, enter the matching full topic URL,
for example https://ntfy.sh/a-long-random-private-name, save, and use Send test
notification. The topic URL is encrypted with Windows DPAPI. Treat the topic
name like a password because anyone who knows a public ntfy.sh topic can
subscribe to it.

HomeWatch checks AdGuard about every 3 seconds while its PowerShell service is
running. A phone alert is sent once when a recent session reaches high
confidence through direct-site and media-delivery evidence. The Alerts panel
records the notification time separately from the acknowledgement time.

LIMITATIONS
-----------
DNS data reveals domains, devices, and timing. HTTPS still prevents HomeWatch
from seeing complete page URLs, search words, video titles, or page contents.

To stop HomeWatch, close its PowerShell window. AdGuard Home continues working.

ENHANCED HISTORY AND CATEGORIES
-------------------------------
The Data settings panel controls local history retention from 1 to 3650 days.
HomeWatch keeps its history separately from AdGuard Home, so AdGuard log
rotation does not remove already-collected HomeWatch events.

An optional HTTPS category-feed URL can update classification once per day.
The JSON document must contain adultSites, adultCdns, and bypassDomains arrays.
See category-feed.example.json for the required format.  A downloaded feed is
validated before it replaces the last working copy.

The Alerts panel summarizes high-confidence sessions and encrypted-DNS signals.
Activity can be exported as CSV or JSON.  DNS evidence does not reveal full
HTTPS URLs, searches, page titles, or definitive viewing duration.

HOMEWATCH 2 HISTORICAL BROWSING
-------------------------------
HomeWatch 2 now retains and browses all successfully imported SQLite history. Its date-range controls include All History and custom dates; older evidence is loaded with cursor pagination instead of fixed 200/500-row result caps. Import checkpoints automatically recover query-log pages after downtime. See HomeWatch2/README.md and docs/ACTIVITY_API.md.
