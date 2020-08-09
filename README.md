# DocFXYAMLToMarkdown

Generates Markdown documentation from DocFX YAML metadata files.

This tool was built to convert the DocFX metadata from Yarn Spinner into Markdown files used in the Hugo-powered site [yarnspinner.dev](https://yarnspinner.dev).

Please note that this is not a general-purpose DocFX-to-Markdown tool. In particular, the generated Markdown may contain references to Hugo shortcodes that are only defined in the Yarn Spinner site. 

This project is released in the hope that others might find it useful.

## Setup

1. Install the [.NET SDK](https://dotnet.microsoft.com/download).
2. Install [DocFX](https://dotnet.github.io/docfx/).

## Usage

1. Generate the DocFX for your projects:
   * `docfx metadata --filter <path to filter .yml> <project1> <project2> ...`
   * This will generate metadata YAML files in the folder `_api`.
2. Generate the Markdown for use in static sites:
   * `dotnet run -- --input-directory <path to the _api folder> --output-directory <output_directory>`
3. The generated Markdown files are now ready for use.

## Options:

* `--input-directory:` The directory containing DocFX-generated YAML metadata.
* `--output-directory:` The directory in which to create Markdown documentation.
* `--overwrite-file-directory:` (Optional) The directory containing [overwrite files](https://dotnet.github.io/docfx/tutorial/intro_overwrite_files.html), which can be used to add or replace content found in the YAML metadata. 
  * Please note that only string properties are supported, and their contents are always replaced by what's found in the overwrite file.

## License

This project is licensed under the terms of the MIT License. For more details, please see the file [LICENSE.md](LICENSE.md).