# Managed ownership probes check release on success and failure, not native COM.
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'office-repeat.ps1'), [ref]$tokens, [ref]$errors)
if ($errors) { throw 'The repeat script does not parse.' }
foreach ($name in @('Open-Document', 'Invoke-ExcelCell')) {
    $definition = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if (-not $definition) { throw "Missing lifetime helper: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}
Add-Type @'
using System;
using System.Collections.Generic;
public class ExcelReferenceProbe : IDisposable {
    public static HashSet<ExcelReferenceProbe> Live = new HashSet<ExcelReferenceProbe>();
    public static Dictionary<int,string> Values = new Dictionary<int,string>();
    public static string FailAt;
    public static ExcelReferenceProbe Workbook = new ExcelReferenceProbe();
    public bool ReadOnly { get { return false; } }
    private int row;
    private static ExcelReferenceProbe Acquire(string stage) {
        if (FailAt == stage) throw new InvalidOperationException(stage);
        var value = new ExcelReferenceProbe(); Live.Add(value); return value;
    }
    public ExcelReferenceProbe Workbooks { get { return Acquire("workbooks"); } }
    public ExcelReferenceProbe Open(string url, int links, bool readOnly) {
        if (FailAt == "open") throw new InvalidOperationException("open");
        return Workbook;
    }
    public ExcelReferenceProbe Worksheets { get { return Acquire("worksheets"); } }
    public ExcelReferenceProbe Item(int index) { return Acquire("worksheet"); }
    public ExcelReferenceProbe Cells { get { return Acquire("cells"); } }
    public ExcelReferenceProbe Item(int row, int column) {
        var cell = Acquire("cell"); cell.row = row; return cell;
    }
    public string Value2 {
        get {
            if (FailAt == "read") throw new InvalidOperationException("read");
            return Values[row];
        }
        set {
            if (FailAt == "write") throw new InvalidOperationException("write");
            Values[row] = value;
        }
    }
    public void Dispose() {
        if (!Live.Remove(this)) throw new InvalidOperationException("Double release");
    }
}
'@
function Release-Com($value) { if ($null -ne $value) { $value.Dispose() } }
$Application = 'Excel'
$documentUrl = 'https://example.test/shared/test.xlsx'
$app = [ExcelReferenceProbe]::new()
foreach ($stage in @('', 'workbooks', 'open', 'worksheets', 'worksheet', 'cells', 'cell', 'write', 'read')) {
    [ExcelReferenceProbe]::FailAt = $stage
    [ExcelReferenceProbe]::Values.Clear()
    $caught = $null
    try {
        Open-Document
        Invoke-ExcelCell -Row 3 -Value 'retained marker'
        $value = Invoke-ExcelCell -Row 3 -Read
        if ($value -isnot [string] -or $value -ne 'retained marker') { throw 'Read lost the value or returned an owned object.' }
    } catch { $caught = $_.Exception.Message }
    if ([ExcelReferenceProbe]::Live.Count -ne 0) { throw "Owned child references leaked at stage '$stage'." }
    if ($stage -and -not $caught) { throw "Expected '$stage' failure was not reported." }
    if ($stage -in @('open', 'worksheet', 'cell', 'write') -and -not $caught.Contains($stage)) { throw "Original '$stage' method failure was lost." }
    if (-not $stage -and $caught) { throw $caught }
    Write-Host "Passed Excel child-reference ownership case: '$stage'"
}
