# Shannon.DevToys.Barcode

Japanese version: [README.ja.md](https://github.com/Shannon-toppo/devtoys-barcode/blob/main/README.ja.md)

## What it is

Shannon.DevToys.Barcode is an extension for DevToys 2.0 that adds a Barcode Encoder / Decoder tool. The tool generates barcodes from text and reads barcodes from images, using the same screen layout as the built-in QR Code tool.

## Features

- Generates barcodes from text and saves them as PNG or SVG. SVG is available for generated barcodes only.
- Reads barcodes from images. Several barcodes in one image are all detected and listed with their type. The first one fills the text box.
- Reads rotated images, tilted barcodes, white-on-black barcodes, low-contrast images, uneven lighting, and noisy or compressed images, to a degree. See Known limits.
- Computes the EAN and UPC check digit when it is omitted, and validates it when it is given.
- Shows a message that states the rule of the selected barcode type when the input is invalid.
- Accepts images from a file picker, drag and drop, or pasting. Works with DevToys Smart Detection for images and text.
- On macOS, also reads HEIC and HEIF pictures, such as iPhone photos. They are converted to PNG with the system's `sips` tool. See Known limits.
- Remembers the selected barcode type. A type detected from an image is selected in the dropdown but is not remembered.
- Accepts non-ASCII text, such as Japanese, in Data Matrix, PDF417 and Aztec. The text is encoded as UTF-8.
- User interface in English and Japanese.

## Supported formats

| Format | Generate | Read |
|---|---|---|
| Code 128 (default) | Yes | Yes |
| EAN-13 | Yes | Yes |
| EAN-8 | Yes | Yes |
| UPC-A | Yes | Yes |
| UPC-E | Yes | Yes |
| Code 39 | Yes | Yes |
| Code 93 | Yes | Yes |
| ITF | Yes | Yes |
| Codabar | Yes | Yes |
| Data Matrix | Yes | Yes |
| PDF417 | Yes | Yes |
| Aztec | Yes | Yes |
| QR Code | No | Yes |

Reading also covers the other formats that ZXing.Net decodes, for example GS1 DataBar. QR Code is not offered for generation because DevToys has a built-in QR tool.

## Installation

Requires DevToys 2.0. The package was developed against DevToys 2.0.9 on macOS. Windows and Linux have not been tested.

1. Download the `.nupkg` file of the package from [NuGet](https://www.nuget.org/packages/Shannon.DevToys.Barcode) or from the [releases page](https://github.com/Shannon-toppo/devtoys-barcode/releases).
2. In DevToys, open Manage extensions, choose Install, select the `.nupkg` file, and restart DevToys.

The extension then appears in the list as "Barcode Encoder / Decoder". This route was verified with DevToys 2.0.9 on macOS.

## Usage

### Generate a barcode

1. Select the barcode type in the Barcode type dropdown.
2. Enter the text. The barcode is generated from it.
3. Save the image as PNG or SVG. Generated images contain no human-readable text under the bars.

For EAN and UPC types, the check digit is calculated when you omit it, and validated when you include it. The text you typed is not rewritten. The encoded value is shown.

If the text does not fit the selected type, a message states the rule of that type.

### Read barcodes

Load an image with the file picker, drag and drop it onto the tool, or paste it. The detected barcodes are listed with their type and text. The first one fills the text box.

## Command line

The extension also adds a `barcode` command for the DevToys command line app (DevToys CLI). It shares its code with the tool in the app, and is covered by unit tests, but it has not yet been run inside DevToys CLI itself.

Generate a barcode. The extension of the output file chooses the format: `.svg`, `.png`, `.jpg`, `.bmp`, `.gif`, `.webp` and the other picture formats DevToys supports.

```
DevToys.CLI barcode --input "Hello-123" --outputfile hello.svg
DevToys.CLI barcode -i 400638133393 -t ean13 -o ean13.png
DevToys.CLI barcode -i labels.txt -t datamatrix -o labels.png
```

`--type` (`-t`) is one of `code128` (the default), `ean13`, `ean8`, `upca`, `upce`, `code39`, `code93`, `itf`, `codabar`, `datamatrix`, `pdf417` and `aztec`. If the text cannot be encoded, the reason and the rule of the type are written to the error output and the exit code is -1.

Read the barcodes in an image. The text of each barcode is written on its own line, from top to bottom, then left to right.

```
DevToys.CLI barcode --input photo.png
DevToys.CLI barcode -i photo.heic --showtype
DevToys.CLI barcode -i photo.png -o result.txt
```

With `--showtype`, each line is the type of the barcode, a tab, then the text. A line break inside a text is written as `\n` when the image holds several barcodes or when `--showtype` is used. If no barcode is found, a message is written to the error output and the exit code is -1.

A text passed to `--input` that looks like the path of a picture that does not exist (for example `photo.png`) is reported as a missing file. To encode such a text, put it in a text file and pass the file.

## Known limits

These limits were measured on synthetic images only, not on photographs.

- HEIC and HEIF pictures are read on macOS only. They are converted with the system's `sips` tool (`/usr/bin/sips`), and nothing is installed for this. On Windows and Linux they are not supported, and the file picker does not offer them. A conversion that fails or takes more than 30 seconds shows the "Cannot read the file" message.
- Strong blur is not read. For the dense one-dimensional types, a Gaussian blur with sigma of about 2 px or more is not read.
- Bars narrower than about 2 px are unreliable.
- Very dark uneven lighting, heavy noise and strong perspective fail for some types.
- Data Matrix and Aztec on a busy background can be missed.
- A Data Matrix or Aztec symbol that sits closer to other content than its own quiet zone (about four modules), or that touches a damaged barcode, can be missed.
- Two identical barcodes in one picture are reported once.
- GS1 application identifiers are not parsed. The raw text is shown.
- On an image with no barcode, a false result is possible but rare. Of about 250 generated test images, one (random noise) produced one.
- Reading a 12 megapixel image with no barcode takes about 1.4 seconds on a 4-core Linux machine.

## Building from source

```
dotnet build
dotnet test
dotnet pack src/DevToys.Barcode -c Release
```

The library targets net8.0. The tests target net10.0.

To try a local build in DevToys on macOS, start DevToys with the `EXTRAPLUGIN` environment variable set to the absolute path of the build output:

```
EXTRAPLUGIN=/absolute/path/to/src/DevToys.Barcode/bin/Debug/net8.0 /Applications/DevToys.app/Contents/MacOS/DevToys
```

## Third-party licenses

ZXing.Net and ZXing.Net.Bindings.ImageSharp.V3 are bundled in the package. Both are licensed under the Apache License 2.0. See [THIRD-PARTY-NOTICES.md](https://github.com/Shannon-toppo/devtoys-barcode/blob/main/THIRD-PARTY-NOTICES.md).

SixLabors.ImageSharp is used through DevToys and is not bundled.

## License

MIT License. See [LICENSE](https://github.com/Shannon-toppo/devtoys-barcode/blob/main/LICENSE).
