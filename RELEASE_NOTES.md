First preview of the Barcode Encoder / Decoder extension for DevToys 2.0.

## Install

Download `Shannon.DevToys.Barcode.0.1.0-preview.1.nupkg` below. In DevToys, open Manage extensions, choose Install, select the file, and restart DevToys.

## What it does

- Generates Code 128, EAN-13, EAN-8, UPC-A, UPC-E, Code 39, Code 93, ITF, Codabar, Data Matrix, PDF417 and Aztec barcodes from text, saved as PNG or SVG.
- Reads those types, QR codes and the other formats ZXing.Net decodes from an image, including several barcodes in one image.
- Reads HEIC and HEIF pictures on macOS.
- Adds a `barcode` command for DevToys CLI (covered by unit tests, not yet run inside DevToys CLI).
- English and Japanese user interface.

See the [README](https://github.com/Shannon-toppo/devtoys-barcode/blob/main/README.md) for the known limits.

Developed and checked with DevToys 2.0.9 on macOS. Windows and Linux have not been tested.
