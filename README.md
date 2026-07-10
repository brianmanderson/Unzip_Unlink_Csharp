# Unzip_Unlink_Csharp

A Windows (WPF) desktop application for rewriting the DICOM identifiers that link
imaging series together. It assigns a fresh Frame of Reference UID to each DICOM
series, so that copied or re-imported studies are treated as independent data rather
than being merged with, or linked to, the originals.

## What it does

- Optionally unzips `.zip` archives found in a directory tree before processing.
- Recursively characterizes a directory, grouping files by DICOM series
  (via SimpleITK GDCM series discovery).
- Generates a new derived UID per series and rewrites the Frame of Reference UID on
  each file (via fo-dicom), keeping a series internally consistent.
- Includes a folder watcher that waits for network file transfers to finish before
  processing, for use as a drop-folder / batch workflow.

## Components

- `UnzipUnlinkGUI` — WPF front end (`MainWindow`), progress reporting, About page.
- `NewFrameOfReferenceClass` — DICOM UID characterization and rewriting logic.
- `Unzip_Unlink` — unlink/move utilities and console entry point.
- `UnzipClass` — zip extraction helpers.
- `FolderWatcher` — filesystem watcher for detecting completed transfers.

## Tech stack

C# / .NET (WPF), SimpleITK (`itk.simple`), fo-dicom (`FellowOakDicom`),
Windows API Code Pack (folder dialogs).

## Installation

Prebuilt releases are available on the
[Releases page](https://github.com/brianmanderson/Unzip_Unlink_Csharp/releases).

## Publication

The methodology behind this tool was published in JACMP; the accepted manuscript
is included under `Paper/JACMP/`.
