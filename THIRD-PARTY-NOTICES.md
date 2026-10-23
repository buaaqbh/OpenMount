# Third-party notices

OpenMount is distributed under the MIT License. It uses or redistributes the following components:

| Component | License | Notes |
| --- | --- | --- |
| rclone | MIT | Bundled as `bin\rclone.exe`; https://github.com/rclone/rclone |
| WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos | GPLv3 with FLOSS exception | OpenMount (MIT) relies on the FLOSS exception: it links only through rclone and redistributes the unmodified official installer. https://github.com/winfsp/winfsp |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| SSH.NET | MIT | SSH host-key scanning for trust-on-first-use only (no file transfer); https://github.com/sshnet/SSH.NET |
| WPF-UI (lepoco) | MIT | Fluent styles for the WPF user interface; https://github.com/lepoco/wpfui |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | Apache-2.0 / MIT | test only |

The WinFsp FLOSS exception requires that this notice and a link to the WinFsp repository appear in the user interface
and in user-facing documentation, that OpenMount remains Free/Libre and Open Source Software, and that it is not linked
or distributed with proprietary software.
