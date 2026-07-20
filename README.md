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

LIMITATIONS
-----------
DNS data reveals domains, devices, and timing. HTTPS still prevents HomeWatch
from seeing complete page URLs, search words, video titles, or page contents.

To stop HomeWatch, close its PowerShell window. AdGuard Home continues working.
