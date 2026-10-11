"""Generate the small Phase 1 test surface (metric, ~300 points).

Writes, next to each other in testdata/:
  small-site.xml   LandXML 1.2 TIN surface "EG" (points + faces + 2 breaklines in SourceData)
  breaklines.scr   AutoCAD script drawing the same breaklines as 3D polylines (fallback if
                   the LandXML import does not bring the breaklines into the surface definition)

Layout: 18 x 17 grid at 5 m spacing (85 m x 80 m), origin E 383000 / N 5776000
(UTM 13N-like magnitudes, so float precision issues show up in testing like on a real job).
A ditch runs east-west along grid row 8, a ridge runs north-south along grid column 5.
Both lie on grid lines, so the regular grid triangulation already honours them.

Usage:  python tools/make_test_surface.py
"""
from pathlib import Path

COLS, ROWS, STEP = 18, 17, 5.0
E0, N0, Z0 = 383000.0, 5776000.0, 500.0
DITCH_ROW, RIDGE_COL = 8, 5
DITCH_DEPTH, RIDGE_HEIGHT = 0.8, 0.6


def elevation(i: int, j: int) -> float:
    x, y = i * STEP, j * STEP
    z = Z0 + 0.02 * x + 0.01 * y           # 2 % east, 1 % north
    z += 0.5 * ((x - 40) ** 2 + (y - 60) ** 2) ** 0.5 / 50   # gentle bowl
    if j == DITCH_ROW:
        z -= DITCH_DEPTH
    if i == RIDGE_COL:
        z += RIDGE_HEIGHT
    return round(z, 3)


def pid(i: int, j: int) -> int:
    return j * COLS + i + 1  # LandXML ids are 1-based


def main() -> None:
    out = Path(__file__).resolve().parent.parent / "testdata"
    out.mkdir(exist_ok=True)

    pts = {(i, j): (E0 + i * STEP, N0 + j * STEP, elevation(i, j)) for j in range(ROWS) for i in range(COLS)}

    faces = []
    for j in range(ROWS - 1):
        for i in range(COLS - 1):
            a, b, c, d = pid(i, j), pid(i + 1, j), pid(i + 1, j + 1), pid(i, j + 1)
            faces += [(a, b, c), (a, c, d)]  # counter-clockwise seen from above

    ditch = [pts[(i, DITCH_ROW)] for i in range(COLS)]
    ridge = [pts[(RIDGE_COL, j)] for j in range(ROWS)]

    # LandXML coordinate order is NORTHING EASTING ELEVATION.
    def nez(p):
        e, n, z = p
        return f"{n:.3f} {e:.3f} {z:.3f}"

    lines = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        '<LandXML xmlns="http://www.landxml.org/schema/LandXML-1.2" version="1.2" date="2026-10-10" time="00:00:00">',
        '  <Units><Metric areaUnit="squareMeter" linearUnit="meter" volumeUnit="cubicMeter" '
        'temperatureUnit="celsius" pressureUnit="milliBars" angularUnit="decimal degrees" directionUnit="decimal degrees"/></Units>',
        '  <Project name="LiveSyncTest"/>',
        '  <Application name="make_test_surface.py" version="1"/>',
        '  <Surfaces>',
        '    <Surface name="EG" desc="LiveSync Phase 1 test surface">',
        '      <SourceData>',
        '        <Breaklines>',
        f'          <Breakline brkType="standard" desc="Ditch"><PntList3D>{" ".join(nez(p) for p in ditch)}</PntList3D></Breakline>',
        f'          <Breakline brkType="standard" desc="Ridge"><PntList3D>{" ".join(nez(p) for p in ridge)}</PntList3D></Breakline>',
        '        </Breaklines>',
        '      </SourceData>',
        '      <Definition surfType="TIN">',
        '        <Pnts>',
    ]
    lines += [f'          <P id="{pid(i, j)}">{nez(pts[(i, j)])}</P>' for j in range(ROWS) for i in range(COLS)]
    lines += ['        </Pnts>', '        <Faces>']
    lines += [f'          <F>{a} {b} {c}</F>' for a, b, c in faces]
    lines += ['        </Faces>', '      </Definition>', '    </Surface>', '  </Surfaces>', '</LandXML>', '']
    (out / "small-site.xml").write_text("\n".join(lines), encoding="utf-8")

    # AutoCAD script: one 3DPOLY per breakline (x,y,z order here), blank line ends each command.
    scr = []
    for poly in (ditch, ridge):
        scr.append("_.3DPOLY")
        scr += [f"{e:.3f},{n:.3f},{z:.3f}" for e, n, z in poly]
        scr.append("")
    (out / "breaklines.scr").write_text("\n".join(scr) + "\n", encoding="ascii")

    zs = [p[2] for p in pts.values()]
    print(f"{len(pts)} points, {len(faces)} faces, z {min(zs):.3f}..{max(zs):.3f} m -> {out}")


if __name__ == "__main__":
    main()
