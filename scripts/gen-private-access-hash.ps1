# Generates a BCrypt hash for PrivateAccess__PasswordHash.
#
# Usage:
#   pwsh scripts/gen-private-access-hash.ps1
#   (you'll be prompted for the password — it is not echoed)
#
# Requires the .NET SDK on PATH. Uses the same BCrypt.Net-Next package
# the API uses, so the hash is guaranteed compatible.

$ErrorActionPreference = "Stop"

$secure = Read-Host -AsSecureString -Prompt "Password"
$BSTR = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $password = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($BSTR)
} finally {
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($BSTR)
}

$tmp = New-Item -ItemType Directory -Path (Join-Path $env:TEMP "postpilot-bcrypt-$([guid]::NewGuid().ToString('N'))")
try {
    Push-Location $tmp.FullName
    & dotnet new console --force | Out-Null
    & dotnet add package BCrypt.Net-Next --version 4.0.3 | Out-Null

    @'
var pwd = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("PP_PWD") ?? "";
Console.WriteLine(BCrypt.Net.BCrypt.HashPassword(pwd, workFactor: 12));
'@ | Set-Content -Path "Program.cs" -Encoding utf8

    $env:PP_PWD = $password
    & dotnet run --nologo --verbosity quiet -- ""
} finally {
    Pop-Location
    Remove-Item -Recurse -Force $tmp.FullName -ErrorAction SilentlyContinue
    $env:PP_PWD = $null
}
