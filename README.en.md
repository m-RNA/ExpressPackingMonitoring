# <img src="ExpressPackingMonitoring/app.ico" align="left" width="128" height="128"/> Express Packing Monitoring

[简体中文](README.md) | English

![GitHub Stars](https://img.shields.io/github/stars/m-RNA/ExpressPackingMonitoring?style=flat&color=ffcf49)
[![GitHub All Releases](https://img.shields.io/github/downloads/m-RNA/ExpressPackingMonitoring/total)](https://github.com/m-RNA/ExpressPackingMonitoring/releases)

A packing video and shipment-risk interception tool for e-commerce sellers and packing stations. It records automatically when a shipping barcode is scanned, integrates with Kuaidi Assistant to announce buyer messages and seller notes, and alerts packers when an order is refunded after its shipping label has already been printed.

> It does more than preserve evidence for disputes: it surfaces special instructions and stops refunded orders before shipment, helping prevent packing mistakes and avoidable losses.

![Application screenshot](Image/软件截图.jpg)

## Who It Is For

- Sellers who print shipping labels with Kuaidi Assistant and want to keep their existing workflow
- Packing stations that need to catch refunds occurring after a label has been printed
- Teams that want buyer messages, seller notes, and product information announced while packing
- Sellers who need hands-free recording and fast retrieval by tracking number
- Warehouses that need video playback from phones or other computers on the LAN
- Users who want to trim the beginning or end of a recording before downloading it
- Computers with limited storage that need automatic cleanup while reserving free disk space

## Main Features

- Integrates with Kuaidi Assistant to sync orders and announce buyer messages, seller notes, and product information
- Checks for post-print refunds asynchronously in shipping and return modes, with status-specific alerts that do not interrupt recording
- Uses the camera to recognize one-dimensional shipping-label barcodes and start recording automatically
- Reads the central guide at a high rate, adds a low-rate full-frame fallback while idle, and restricts recognition to the guide while recording to reduce product-barcode false triggers
- Keeps camera recognition and keyboard-mode scanners available together, so a scanner can remain as a background-input and recovery fallback
- Supports camera recording, audio capture, and video watermarks
- Searches recordings by order or tracking number and plays them in a browser
- Provides browser-based trim-and-download with a selectable time range
- Supports multiple storage locations, automatic drive switching, and reserve-space-based cleanup
- Checks for updates through the launcher, verifies incremental packages, and installs pending updates on the next launch; manually downloaded patches include a double-click CMD installer

## Requirements

- Windows 10/11 x64
- USB camera
- Barcode scanner configured as a keyboard input device (optional, but recommended as a fallback)

The portable release normally includes the required .NET runtime and `ffmpeg.exe`. Running from source or developing the project requires the .NET 8 SDK and `ffmpeg.exe` (the Full build is recommended).

## Quick Start

1. Open the application and go to Settings.
2. Select the camera and microphone.
3. Choose recording locations and the amount of disk space to reserve for the system.
4. Place the shipping-label barcode inside the guide until it is recognized, or use the existing scanner workflow.
5. Finish the shipment or scan the stop command to end recording.
6. Enter the tracking number in the recording list whenever you need to retrieve the video.

Placing a label in view does not wake an idle camera. Click the application, press a key, or use the scanner first, then place the label inside the recognition guide.

## Updating

- Always start the app from the root `ExpressPackingMonitoring.exe`. The launcher downloads verified incremental packages in the background and installs them on the next launch.
- For a manually downloaded patch, extract the whole archive and double-click `双击安装增量更新.cmd`. It reads the saved `app` directory from the user configuration, closes the running app normally, verifies every file, and rolls back if installation fails.
- Use the full package if the installed version is below the patch baseline, the app directory has not been recorded yet, or the launcher itself must be updated. Keep `%LOCALAPPDATA%\ExpressPackingMonitoring\` to preserve configuration and database records.

## LAN Playback

1. Enable the Web service in Settings.
2. Save the settings and restart the application.
3. On another device in the same LAN, open `http://COMPUTER_IP:5280`.

Allow network access if Windows Firewall prompts you.

![LAN Web playback](Image/WebService.jpg)

## Order Note Announcements

This feature uses the included browser userscript:

1. Install Tampermonkey or Violentmonkey.
2. Install `Scripts/快递助手订单推送.user.js` from this repository.
3. When the printing page opens or its orders change, the script sends the current order information to the monitoring workstation automatically. Normal order syncing does not depend on the refund worker page.
4. The monitoring workstation can announce buyer messages, seller notes, and product information.
5. To enable post-print refund alerts, keep one signed-in Kuaidi Assistant batch-printing page open. The script opens a background refund verification worker without taking focus. Only this worker changes the official post-print-refund filter; the page being used by the operator is not changed.
6. After a scan, recording starts immediately while refund data is requested asynchronously. The worker first returns the current refund list. If the tracking number is absent, it performs an exact historical lookup. When the printing workstation is offline or the lookup fails, the monitor falls back to order data retained in SQLite for 90 days.

The refund worker has a dedicated title and translucent overlay. Do not operate it manually. If it is closed accidentally, the script recreates it automatically; it can also be reopened from the userscript menu.

When the userscript connects to a new monitor address for the first time, the browser may request cross-origin access. Confirm that the destination is the local computer or a trusted LAN workstation before allowing it. Reinstalling the script through the monitor's setup guide adds an exact permission for the current workstation.

Duplicate tracking numbers are checked against non-deleted recording records from the last 30 days, independently of the browser cache. Order and refund caches are stored in SQLite. The legacy `orderinfo_cache.json` file is migrated during an upgrade and removed afterward.

## Recording Storage

Configuration, databases, logs, and recordings are stored in the current user's local data directories. Existing settings and recording records are preserved during normal upgrades as long as the user data is not deleted.

Storage settings represent reserved free space, not a recording quota. When a drive falls below its reserve threshold, the application stops writing new recordings to that drive and prefers the next configured location. The system drive automatically receives a larger safety reserve to protect Windows and other applications.

## License

This project is open source under the [AGPL-3.0 License](LICENSE).

Personal learning and use in your own store are free. If you distribute a modified version or provide it as a network service, you must comply with the source-sharing requirements of AGPL-3.0.

![Packing station scenario](Image/场景图.jpg)
