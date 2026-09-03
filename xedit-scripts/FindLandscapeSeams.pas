{
  FindLandscapeSeams.pas

  READ-ONLY diagnostic. Makes no changes to any record or file.

  For every pair of adjacent exterior cells in your current selection, this
  decodes each cell's winning LAND heightmap (VHGT) and checks whether the
  vertices along the shared edge agree in absolute height. Where they don't,
  that's a landscape seam/hole in-game. Writes a CSV report you can sort by
  severity.

  VHGT has no sub-elements in xEdit's model - it's read here as a raw
  1096-byte array (4-byte little-endian float Offset, then 1089 signed
  delta bytes for the 33x33 grid, then 3 unused bytes), decoded by hand.
  This was confirmed against real data from Step0_DumpCellLandStructure.pas
  (decoded offsets came out to clean round numbers: -796.0, -461.0, -1247.0).

  Data is kept in parallel plain arrays (ints, floats) plus one TStringList
  for plugin names, rather than a big array of records containing strings -
  the record-with-string version reliably crashed xEdit itself ("Invalid
  pointer operation") once run across a few thousand real cells, even
  though every individual VHGT decode was already bounds-checked. This
  layout avoids that pattern.

  HOW TO RUN
  1. In xEdit, right-click the worldspace block(s) you care about (or the
     whole load order) -> Apply Filter -> filter to records with
     Signature = CELL, and (recommended) restrict to the worldspace you're
     checking (e.g. Tamriel) to keep the run fast.
  2. Select ALL the filtered CELL records (Ctrl+A in the results).
  3. Right-click the selection -> Apply Script... -> pick this script -> OK.
  4. When it finishes, check the Message Log for the report file path,
     then open LandscapeSeamReport.csv.
     Progress checkpoints print every 200 cells - if xEdit crashes again,
     the LAST checkpoint printed tells us roughly where.

  CSV COLUMNS
  Edge          - "East" or "North": which shared border this row describes
  CellA_X/Y     - grid coords of the first cell
  PluginA       - plugin that owns the winning LAND override for cell A
  CellB_X/Y     - grid coords of the neighboring cell
  PluginB       - plugin that owns the winning LAND override for cell B
  SamePlugin    - TRUE if both cells' winning LAND come from the same plugin
                  (still worth checking, but cross-plugin rows are the
                  classic "two mods, one seam" case)
  MaxDeltaUnits - worst mismatch along the shared edge, in game height units
                  (8 units = 1 raw heightmap step; ignore tiny values, they
                  are floating point noise, not real seams)
  WorstVertex   - index 0-32 along the edge where the mismatch is largest
}
unit UserScript;

interface

implementation

const
  cToleranceUnits = 8.0;
  cMaxCells = 4000; // vanilla Tamriel has ~3600 landscaped cells
  cReportFileName = 'LandscapeSeamReport.csv';
  cProgressEvery = 200;

var
  // Parallel arrays instead of array-of-record-with-string (see header note).
  // Bound is a literal (3999) because this Pascal Script dialect rejects a
  // named const as a static array bound - must match cMaxCells above.
  CellX: array[0..3999] of integer;
  CellY: array[0..3999] of integer;
  CellFormID: array[0..3999] of cardinal;
  CellHeights: array[0..3999, 0..32, 0..32] of single;
  CellPlugin: TStringList; // CellPlugin[idx] = plugin name for cell idx
  CellCount: integer;
  Report: TStringList;
  SkippedNoLand, SkippedNoVhgt, SkippedDupe, SkippedBadVhgtSize: integer;

function TryFindLandInContainer(container: IInterface): IInterface;
var
  i: integer;
  rec: IInterface;
begin
  Result := nil;
  if not Assigned(container) then exit;
  for i := 0 to ElementCount(container) - 1 do begin
    rec := ElementByIndex(container, i);
    if Signature(rec) = 'LAND' then begin
      Result := rec;
      exit;
    end;
  end;
end;

function GetLandFromCell(cellRecord: IInterface): IInterface;
var
  grp, sub: IInterface;
  i: integer;
begin
  Result := nil;
  grp := ChildGroup(cellRecord);
  if not Assigned(grp) then exit;

  // LAND usually sits one level deeper, inside the cell's "Temporary" subgroup
  Result := TryFindLandInContainer(grp);
  if Assigned(Result) then exit;

  for i := 0 to ElementCount(grp) - 1 do begin
    sub := ElementByIndex(grp, i);
    Result := TryFindLandInContainer(sub);
    if Assigned(Result) then exit;
  end;
end;

function FindCellIndex(x, y: integer): integer;
var
  i: integer;
begin
  Result := -1;
  for i := 0 to CellCount - 1 do
    if (CellX[i] = x) and (CellY[i] = y) then begin
      Result := i;
      exit;
    end;
end;

function AlreadySeen(fid: cardinal): boolean;
var
  i: integer;
begin
  Result := false;
  for i := 0 to CellCount - 1 do
    if CellFormID[i] = fid then begin
      Result := true;
      exit;
    end;
end;

function Pow2(n: integer): double;
var
  i: integer;
  r: double;
begin
  r := 1.0;
  if n >= 0 then begin
    for i := 1 to n do r := r * 2.0;
  end
  else begin
    for i := 1 to -n do r := r / 2.0;
  end;
  Result := r;
end;

// Extracts sign/exponent/mantissa straight from the bytes (no 32-bit
// assembly step) so every intermediate value stays well under 2^31 and
// no Int64 is needed (this Pascal Script dialect has no Int64 type).
function BytesToSingleLE(b0, b1, b2, b3: integer): double;
var
  sign, exponent, mantissa: integer;
begin
  if (b3 and $80) <> 0 then sign := -1 else sign := 1;
  exponent := ((b3 and $7F) shl 1) or ((b2 and $80) shr 7);
  mantissa := ((b2 and $7F) shl 16) or (b1 shl 8) or b0;
  if exponent = 0 then
    Result := sign * (mantissa / 8388608.0) * Pow2(-126)
  else
    Result := sign * (1.0 + mantissa / 8388608.0) * Pow2(exponent - 127);
end;

function SignedByte(b: integer): integer;
begin
  if b > 127 then
    Result := b - 256
  else
    Result := b;
end;

// Returns false (and touches nothing in CellHeights[idx]) if this record's
// VHGT isn't the standard 1096-byte layout, instead of blindly indexing
// past its actual bounds - some plugin's landscape data may be malformed,
// and one bad cell must not abort the whole batch.
function DecodeHeights(idx: integer; land: IInterface): boolean;
var
  vhgt: IInterface;
  v: variant;
  lo, hi, base: integer;
  vOffset: double;
  row, col: integer;
  delta: integer;
begin
  Result := false;
  vhgt := ElementBySignature(land, 'VHGT');
  v := GetNativeValue(vhgt);
  lo := VarArrayLowBound(v, 1);
  hi := VarArrayHighBound(v, 1);

  if (hi - lo + 1) <> 1096 then begin
    AddMessage('  Skipping cell (' + IntToStr(CellX[idx]) + ',' + IntToStr(CellY[idx]) +
      '): VHGT is ' + IntToStr(hi - lo + 1) + ' bytes, expected 1096.');
    exit;
  end;

  vOffset := BytesToSingleLE(
    integer(v[lo + 0]), integer(v[lo + 1]),
    integer(v[lo + 2]), integer(v[lo + 3]));

  base := lo + 4; // start of the 33x33 delta grid, after the 4-byte offset
  for row := 0 to 32 do begin
    for col := 0 to 32 do begin
      delta := SignedByte(integer(v[base + row * 33 + col]));
      if col = 0 then begin
        if row = 0 then
          CellHeights[idx][0][0] := vOffset + delta * 8.0
        else
          CellHeights[idx][row][0] := CellHeights[idx][row - 1][0] + delta * 8.0;
      end
      else
        CellHeights[idx][row][col] := CellHeights[idx][row][col - 1] + delta * 8.0;
    end;
  end;
  Result := true;
end;

function Initialize: integer;
begin
  CellCount := 0;
  SkippedNoLand := 0;
  SkippedNoVhgt := 0;
  SkippedDupe := 0;
  SkippedBadVhgtSize := 0;
  CellPlugin := TStringList.Create;
  Report := TStringList.Create;
  // Append across runs: if a report from an earlier (smaller) batch already
  // exists, load it so this run's results add to it instead of overwriting -
  // lets you scan a big worldspace in several smaller, crash-safer chunks.
  if FileExists(cReportFileName) then
    Report.LoadFromFile(cReportFileName)
  else
    Report.Add('Edge,CellA_X,CellA_Y,PluginA,CellB_X,CellB_Y,PluginB,SamePlugin,MaxDeltaUnits,WorstVertex');
  Result := 0;
end;

function Process(e: IInterface): integer;
var
  cell, land, winLand, xclc: IInterface;
  x, y: integer;
  fid: cardinal;
  idx: integer;
begin
  Result := 0;
  if Signature(e) <> 'CELL' then exit;

  fid := FixedFormID(e);
  if AlreadySeen(fid) then begin
    Inc(SkippedDupe);
    exit;
  end;

  cell := WinningOverride(e);
  xclc := ElementByPath(cell, 'XCLC');
  if not Assigned(xclc) then exit; // interior cell, skip
  x := GetElementNativeValues(cell, 'XCLC\X');
  y := GetElementNativeValues(cell, 'XCLC\Y');

  land := GetLandFromCell(e);
  if not Assigned(land) then begin
    Inc(SkippedNoLand);
    exit;
  end;

  winLand := WinningOverride(land);
  if not Assigned(ElementBySignature(winLand, 'VHGT')) then begin
    Inc(SkippedNoVhgt);
    exit;
  end;

  if CellCount >= cMaxCells then begin
    AddMessage('cMaxCells exceeded - increase the constant/array bounds and re-run on a smaller selection.');
    exit;
  end;

  idx := CellCount;
  CellX[idx] := x;
  CellY[idx] := y;
  CellFormID[idx] := fid;
  CellPlugin.Add(GetFileName(GetFile(winLand)));
  if DecodeHeights(idx, winLand) then begin
    Inc(CellCount);
    if (CellCount mod cProgressEvery) = 0 then
      AddMessage('  ...processed ' + IntToStr(CellCount) + ' cells so far');
  end
  else
    Inc(SkippedBadVhgtSize);
end;

procedure CompareEdge(aIdx, bIdx: integer; edgeName: string);
var
  v: integer;
  hA, hB, d, maxD: single;
  worst: integer;
  pluginA, pluginB: string;
begin
  maxD := 0;
  worst := -1;
  for v := 0 to 32 do begin
    if edgeName = 'East' then begin
      hA := CellHeights[aIdx][v][32];
      hB := CellHeights[bIdx][v][0];
    end
    else begin
      hA := CellHeights[aIdx][32][v];
      hB := CellHeights[bIdx][0][v];
    end;
    d := Abs(hA - hB);
    if d > maxD then begin
      maxD := d;
      worst := v;
    end;
  end;

  if maxD > cToleranceUnits then begin
    pluginA := CellPlugin[aIdx];
    pluginB := CellPlugin[bIdx];
    Report.Add(edgeName + ',' +
      IntToStr(CellX[aIdx]) + ',' + IntToStr(CellY[aIdx]) + ',' + pluginA + ',' +
      IntToStr(CellX[bIdx]) + ',' + IntToStr(CellY[bIdx]) + ',' + pluginB + ',' +
      BoolToStr(pluginA = pluginB, true) + ',' +
      FormatFloat('0.0', maxD) + ',' + IntToStr(worst));
  end;
end;

function Finalize: integer;
var
  i, eastIdx, northIdx: integer;
  outPath: string;
  seamCount: integer;
begin
  Result := 0;
  AddMessage('Collected ' + IntToStr(CellCount) + ' landscaped cells. ' +
    'Skipped: ' + IntToStr(SkippedNoLand) + ' no-LAND, ' +
    IntToStr(SkippedNoVhgt) + ' no-VHGT, ' + IntToStr(SkippedDupe) + ' duplicate selections, ' +
    IntToStr(SkippedBadVhgtSize) + ' non-standard VHGT size.');

  for i := 0 to CellCount - 1 do begin
    eastIdx := FindCellIndex(CellX[i] + 1, CellY[i]);
    if eastIdx >= 0 then
      CompareEdge(i, eastIdx, 'East');

    northIdx := FindCellIndex(CellX[i], CellY[i] + 1);
    if northIdx >= 0 then
      CompareEdge(i, northIdx, 'North');
  end;

  seamCount := Report.Count - 1;
  outPath := cReportFileName;
  Report.SaveToFile(outPath);
  AddMessage('Found ' + IntToStr(seamCount) + ' seam edges above ' +
    FormatFloat('0.0', cToleranceUnits) + ' unit tolerance.');
  AddMessage('Report written to: ' + outPath);

  FreeAndNil(Report);
  FreeAndNil(CellPlugin);
end;

end.
