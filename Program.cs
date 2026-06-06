using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Mega65IffConverter;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public sealed class MainForm : Form
{
    private const int TargetWidth = 320;
    private const int TargetHeight = 200;

    // Change this to 4 if you want the app to generate 16-color IFFs.
    // Leave it at 8 for 256-color IFFs.
    private const int PlanesToUse = 8;

    private const string FitMode = "crop";

    private readonly PictureBox _preview;
    private readonly Button _loadButton;
    private readonly Button _convertButton;
    private readonly Label _statusLabel;

    private string? _inputFile;

    public MainForm()
    {
        Text = "PNG to MEGA65 IFF Converter";
        Width = 950;
        Height = 700;
        StartPosition = FormStartPosition.CenterScreen;

        _preview = new PictureBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black
        };

        _loadButton = new Button
        {
            Text = "Load PNG File",
            Width = 150,
            Height = 38,
            Left = 10,
            Top = 10
        };

        _convertButton = new Button
        {
            Text = "Convert For MEGA65",
            Width = 300,
            Height = 38,
            Left = 170,
            Top = 10,
            Enabled = false
        };

        _statusLabel = new Label
        {
            Text = "No file loaded.",
            AutoSize = false,
            Left = 480,
            Top = 16,
            Width = 550,
            Height = 30
        };

        var topPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 60
        };

        topPanel.Controls.Add(_loadButton);
        topPanel.Controls.Add(_convertButton);
        topPanel.Controls.Add(_statusLabel);

        Controls.Add(_preview);
        Controls.Add(topPanel);

        _loadButton.Click += LoadButton_Click;
        _convertButton.Click += ConvertButton_Click;
    }

    private void LoadButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Load PNG File",
            Filter = "PNG files (*.png)|*.png|Image files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _inputFile = dialog.FileName;

        _preview.Image?.Dispose();

        using var loaded = new Bitmap(_inputFile);
        _preview.Image = new Bitmap(loaded);

        _convertButton.Enabled = true;
        _statusLabel.Text = Path.GetFileName(_inputFile);
    }

    private void ConvertButton_Click(object? sender, EventArgs e)
    {
        if (_inputFile == null)
            return;

        string defaultOutputFile = Path.Combine(
            Path.GetDirectoryName(_inputFile)!,
            Path.GetFileNameWithoutExtension(_inputFile) + ".iff"
        );

        using var saveDialog = new SaveFileDialog
        {
            Title = "Save MEGA65 IFF File",
            Filter = "IFF files (*.iff)|*.iff|All files (*.*)|*.*",
            FileName = Path.GetFileName(defaultOutputFile),
            InitialDirectory = Path.GetDirectoryName(_inputFile),
            OverwritePrompt = true
        };

        if (saveDialog.ShowDialog(this) != DialogResult.OK)
            return;

        string outputFile = saveDialog.FileName;

        try
        {
            Cursor = Cursors.WaitCursor;
            _convertButton.Enabled = false;
            _statusLabel.Text = "Converting...";

            PythonConverter.Run(
                inputFile: _inputFile,
                outputFile: outputFile,
                width: TargetWidth,
                height: TargetHeight,
                planes: PlanesToUse,
                fitMode: FitMode
            );

            if (!File.Exists(outputFile))
                throw new FileNotFoundException("The converter completed, but the output file was not created.", outputFile);

            long fileSize = new FileInfo(outputFile).Length;
            string mega65FileName = Path.GetFileName(outputFile).ToUpperInvariant();

            _statusLabel.Text = $"Saved {mega65FileName} using {PlanesToUse} planes.";

            MessageBox.Show(
                this,
                $"done!\n\nSaved to:\n{outputFile}\n\nIFF size: {fileSize:N0} bytes\nBitplanes: {PlanesToUse}\n\nUse this on the MEGA65:\n\n10 SCREEN {TargetWidth},{TargetHeight},{PlanesToUse}\n20 LOADIFF \"{mega65FileName}\"",
                "Mega65 IFF",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Conversion failed.";

            MessageBox.Show(
                this,
                ex.ToString(),
                "Conversion failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
        finally
        {
            Cursor = Cursors.Default;
            _convertButton.Enabled = _inputFile != null;
        }
    }
}

public static class PythonConverter
{
    public static void Run(
        string inputFile,
        string outputFile,
        int width,
        int height,
        int planes,
        string fitMode)
    {
        string tempScript = Path.Combine(
            Path.GetTempPath(),
            "png2mega65iff_embedded.py"
        );

        File.WriteAllText(
            tempScript,
            EmbeddedPythonConverter.Source,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );

        string[] args =
        {
            tempScript,
            inputFile,
            outputFile,
            "--width", width.ToString(),
            "--height", height.ToString(),
            "--planes", planes.ToString(),
            "--fit", fitMode
        };

        RunPython(args);
    }

    private static void RunPython(string[] args)
    {
        // Try Windows Python launcher first.
        PythonRunResult pyResult = TryRunPython("py", usePyLauncher: true, args);

        if (pyResult.ExitCode == 0)
            return;

        // Fallback to python.exe from PATH.
        PythonRunResult pythonResult = TryRunPython("python", usePyLauncher: false, args);

        if (pythonResult.ExitCode == 0)
            return;

        throw new InvalidOperationException(
            "Could not run the embedded Python converter.\n\n" +
            "Make sure Python is installed and Pillow is installed:\n\n" +
            "    pip install pillow\n\n" +
            "py result:\n" +
            pyResult.ToDisplayText() +
            "\n\npython result:\n" +
            pythonResult.ToDisplayText()
        );
    }

    private static PythonRunResult TryRunPython(string executable, bool usePyLauncher, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (usePyLauncher)
                psi.ArgumentList.Add("-3");

            foreach (string arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);

            if (process == null)
            {
                return new PythonRunResult(
                    ExitCode: -1,
                    StandardOutput: "",
                    StandardError: "Process.Start returned null."
                );
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            process.WaitForExit();

            return new PythonRunResult(
                ExitCode: process.ExitCode,
                StandardOutput: stdout,
                StandardError: stderr
            );
        }
        catch (Exception ex)
        {
            return new PythonRunResult(
                ExitCode: -999,
                StandardOutput: "",
                StandardError: ex.ToString()
            );
        }
    }

    private sealed record PythonRunResult(
        int ExitCode,
        string StandardOutput,
        string StandardError)
    {
        public string ToDisplayText()
        {
            return
                $"Exit code: {ExitCode}\n\n" +
                $"STDOUT:\n{StandardOutput}\n\n" +
                $"STDERR:\n{StandardError}";
        }
    }
}

public static class EmbeddedPythonConverter
{
    public const string Source = """"
#!/usr/bin/env python3
import argparse
import struct
from pathlib import Path
from PIL import Image


def parse_color(value: str) -> tuple[int, int, int]:
    value = value.strip()
    if value.startswith("#"):
        value = value[1:]
    if len(value) != 6:
        raise argparse.ArgumentTypeError("Color must be RRGGBB or #RRGGBB")
    return tuple(int(value[i:i + 2], 16) for i in (0, 2, 4))


def iff_chunk(chunk_id: bytes, payload: bytes) -> bytes:
    data = chunk_id + struct.pack(">I", len(payload)) + payload
    if len(payload) & 1:
        data += b"\0"
    return data


def fit_image(img: Image.Image, width: int, height: int, fit: str, bg: tuple[int, int, int]) -> Image.Image:
    img = img.convert("RGBA")
    base = Image.new("RGBA", img.size, bg + (255,))
    img = Image.alpha_composite(base, img).convert("RGB")

    src_w, src_h = img.size

    if fit == "stretch":
        return img.resize((width, height), Image.Resampling.LANCZOS)

    if fit == "crop":
        scale = max(width / src_w, height / src_h)
        new_w = round(src_w * scale)
        new_h = round(src_h * scale)
        resized = img.resize((new_w, new_h), Image.Resampling.LANCZOS)
        left = (new_w - width) // 2
        top = (new_h - height) // 2
        return resized.crop((left, top, left + width, top + height))

    if fit == "contain":
        scale = min(width / src_w, height / src_h)
        new_w = round(src_w * scale)
        new_h = round(src_h * scale)
        resized = img.resize((new_w, new_h), Image.Resampling.LANCZOS)
        canvas = Image.new("RGB", (width, height), bg)
        canvas.paste(resized, ((width - new_w) // 2, (height - new_h) // 2))
        return canvas

    raise ValueError(f"Unknown fit mode: {fit}")


def quantize_image(img: Image.Image, colors: int, dither: bool) -> Image.Image:
    dither_mode = Image.Dither.FLOYDSTEINBERG if dither else Image.Dither.NONE

    return img.quantize(
        colors=colors,
        method=Image.Quantize.MEDIANCUT,
        dither=dither_mode
    )


def get_palette_rgb(indexed: Image.Image, colors: int) -> bytes:
    pal = indexed.getpalette()
    if pal is None:
        raise ValueError("Indexed image has no palette")

    pal = pal[:colors * 3]
    if len(pal) < colors * 3:
        pal += [0] * ((colors * 3) - len(pal))

    return bytes(pal)


def byterun1_encode(data: bytes) -> bytes:
    """
    Amiga IFF ILBM ByteRun1 RLE.

    Control byte meaning:
      0..127    : copy next n+1 literal bytes
      129..255  : repeat next byte 257-n times
      128       : no-op, unused here
    """
    out = bytearray()
    i = 0
    n = len(data)

    while i < n:
        run_len = 1
        while (
            i + run_len < n
            and run_len < 128
            and data[i + run_len] == data[i]
        ):
            run_len += 1

        if run_len >= 3:
            out.append(257 - run_len)
            out.append(data[i])
            i += run_len
            continue

        lit_start = i
        i += run_len

        while i < n:
            look_run = 1
            while (
                i + look_run < n
                and look_run < 128
                and data[i + look_run] == data[i]
            ):
                look_run += 1

            if look_run >= 3:
                break

            i += look_run

            if i - lit_start >= 128:
                break

        literal = data[lit_start:i]
        out.append(len(literal) - 1)
        out.extend(literal)

    return bytes(out)


def make_planar_rows(indexed: Image.Image, width: int, height: int, planes: int) -> list[bytes]:
    """
    Returns ILBM plane rows:
      row 0 plane 0
      row 0 plane 1
      ...
      row 0 plane N
      row 1 plane 0
      ...

    Each plane row is padded to an even byte count.
    """
    pixels = indexed.load()
    row_bytes = ((width + 15) // 16) * 2
    rows = []

    for y in range(height):
        for plane in range(planes):
            row = bytearray(row_bytes)

            byte_index = 0
            current = 0
            bit = 7

            for x in range(width):
                color_index = pixels[x, y]

                if (color_index >> plane) & 1:
                    current |= 1 << bit

                bit -= 1

                if bit < 0:
                    row[byte_index] = current
                    byte_index += 1
                    current = 0
                    bit = 7

            if bit != 7 and byte_index < row_bytes:
                row[byte_index] = current

            rows.append(bytes(row))

    return rows


def make_ilbm_body(indexed: Image.Image, width: int, height: int, planes: int) -> bytes:
    """
    MEGA65 LOADIFF expects ByteRun1-compressed BODY data.
    Compress each bitplane row independently.
    """
    body = bytearray()

    for row in make_planar_rows(indexed, width, height, planes):
        body.extend(byterun1_encode(row))

    return bytes(body)


def write_ilbm(output_path: Path, indexed: Image.Image, width: int, height: int, planes: int) -> None:
    colors = 1 << planes

    bmhd = struct.pack(
        ">HHhhBBBBHBBhh",
        width,
        height,
        0,
        0,
        planes,
        0,
        1,
        0,
        0,
        1,
        1,
        width,
        height
    )

    cmap = get_palette_rgb(indexed, colors)
    body = make_ilbm_body(indexed, width, height, planes)

    form_payload = (
        b"ILBM" +
        iff_chunk(b"BMHD", bmhd) +
        iff_chunk(b"CMAP", cmap) +
        iff_chunk(b"BODY", body)
    )

    output_path.write_bytes(
        b"FORM" + struct.pack(">I", len(form_payload)) + form_payload
    )


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Convert PNG/JPEG/etc to MEGA65 BASIC LOADIFF-compatible IFF ILBM."
    )

    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)

    parser.add_argument("--width", type=int, default=320)
    parser.add_argument("--height", type=int, default=200)
    parser.add_argument("--planes", type=int, default=8, choices=range(1, 9))

    parser.add_argument(
        "--fit",
        choices=["crop", "contain", "stretch"],
        default="crop"
    )

    parser.add_argument(
        "--background",
        type=parse_color,
        default=(0, 0, 0)
    )

    parser.add_argument(
        "--dither",
        action="store_true",
        help="Enable Floyd-Steinberg dithering. Default is off."
    )

    parser.add_argument(
        "--preview",
        type=Path,
        default=None,
        help="Optional output PNG preview of the converted palette image."
    )

    args = parser.parse_args()

    if args.width == 640 and args.height == 400 and args.planes > 4:
        raise SystemExit("MEGA65 640x400 should use 4 planes / 16 colors or less.")

    colors = 1 << args.planes

    img = Image.open(args.input)
    img = fit_image(img, args.width, args.height, args.fit, args.background)
    indexed = quantize_image(img, colors, args.dither)

    write_ilbm(args.output, indexed, args.width, args.height, args.planes)

    if args.preview:
        indexed.convert("RGB").save(args.preview)

    print(f"Wrote {args.output}")
    print(f"{args.width}x{args.height}, {args.planes} planes, {colors} colors")
    print()
    print("MEGA65 BASIC:")
    print(f"10 SCREEN {args.width},{args.height},{args.planes}")
    print(f'20 LOADIFF "{args.output.name.upper()}"')


if __name__ == "__main__":
    main()
"""";
}