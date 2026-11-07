# Compiles Glass.hlsl to Glass.ps (pixel shader 3.0) with the d3dcompiler_47.dll that ships with Windows.
# Run after editing the shader:  powershell -NoProfile -ExecutionPolicy Bypass -File Shaders\compile.ps1
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Add-Type @"
using System; using System.Runtime.InteropServices;
[ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ID3DBlob { [PreserveSig] IntPtr GetBufferPointer(); [PreserveSig] IntPtr GetBufferSize(); }
public static class Fxc {
  [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
  static extern int D3DCompile(byte[] src, IntPtr len, string name, IntPtr defines, IntPtr include,
      string entry, string target, uint flags1, uint flags2, out ID3DBlob code, out ID3DBlob errors);
  static byte[] Bytes(ID3DBlob b) { var a = new byte[(int)b.GetBufferSize()]; Marshal.Copy(b.GetBufferPointer(), a, 0, a.Length); return a; }
  public static byte[] Compile(byte[] src, out string errors) {
    ID3DBlob code, err;
    int hr = D3DCompile(src, (IntPtr)src.Length, "Glass.hlsl", IntPtr.Zero, IntPtr.Zero, "main", "ps_3_0", 1u << 15, 0, out code, out err);
    errors = err != null ? System.Text.Encoding.ASCII.GetString(Bytes(err)) : "";
    return hr >= 0 ? Bytes(code) : null;
  }
}
"@
$src = [IO.File]::ReadAllBytes("$here\Glass.hlsl")
$errors = ""
$ps = [Fxc]::Compile($src, [ref]$errors)
if ($errors) { Write-Host $errors }
if ($ps -eq $null) { Write-Error "Shader did not compile"; exit 1 }
[IO.File]::WriteAllBytes("$here\Glass.ps", $ps)
Write-Host "Glass.ps: $($ps.Length) bytes"
