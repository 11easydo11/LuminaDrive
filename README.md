# Lumina Drive

**Lumina Drive — USB Recovery & Diagnostic Suite** is a Windows desktop utility for checking, repairing, and managing USB storage. The interface is available in Russian, English, and Ukrainian, with dark, light, and Windows system themes.

## Features

- Detect USB storage devices and inspect their partitions and volumes.
- Run storage diagnostics and capacity checks.
- Copy accessible files from a USB drive to a chosen folder on the PC.
- Select PC files and copy them to the chosen USB drive without overwriting existing files.
- Restore a USB drive's partition layout and format it after explicit disk confirmation.
- Export diagnostic reports and review operation history.

## Download

Download **LuminaDrive.exe** from the Assets section of the latest [GitHub Release](../../releases/latest). The release is a self-contained, single-file `win-x64` application and does not require a separate .NET Runtime installation.

## Use

1. Start Lumina Drive and accept the first-run privacy and disk-access notice.
2. Select the USB drive from the list at the top of the window.
3. Open **Drive Files** to choose a PC folder for backup or select PC files to copy to the USB drive.
4. Before any recovery or erase operation, check the displayed disk number and model carefully. These operations require administrator rights and explicit confirmation.

File copying only reads and writes files on mounted Windows volumes. The erase/rebuild action removes the selected drive's existing partitions and creates a new exFAT volume. It uses a quick format and does **not** securely overwrite every physical sector, so deleted data may remain recoverable.

## System requirements

- Windows 10 or Windows 11, x64.
- Administrator rights are needed only for disk repair, formatting, and recovery actions.

Created by Danyil Sokolov (EASY_DO).
