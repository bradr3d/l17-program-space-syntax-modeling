# Program input template

The DAL reads a room program from an Excel workbook (`.xlsx` / `.xlsm`) or a CSV file. Both use
the same columns as the test workbook's `ProgramInfo` sheet. A working example is
`tests/Psm.Data.Tests/Fixtures/program.sample.csv`.

## Columns

Header names are matched case-insensitively. Extra spaces and spaces around `/` are ignored.
Column order doesn't matter, and unknown columns are reported (I002) and ignored.

| Column | Required | Meaning | Example |
|---|---|---|---|
| Dept No | yes | Room id: numbers separated by dots. `x.y` is a room, `x.y.z` belongs to room `x.y`. | `1.1`, `1.1.2`, `7.10` |
| Program Element | yes | Room name | `Classroom - K` |
| Room Qty/ Units | yes | Number of rooms (whole number ≥ 0). `0` = absorbed into other spaces. | `4` |
| Net SF/ Units | yes | Area of **one** room, in SF | `850` |
| Conn. Primary | yes (may be blank) | Dept No of the room this room should be next to. Blank or `0` = none. | `1.2` |
| Total Program SF | no | Qty × Net SF, used as a cross-check (W001) | `3400` |
| Design Capacity / Student Stations | no | Stations per room | `16` |
| Total Stations | no | Read but not used | `64` |
| Notes/Comments | no | Free text. `0` = empty. | |
| Flr | no | Preferred floor (whole number ≥ 1). Blank = no preference. | `1` |
| Color | no (Excel) | Room color, given as the cell **fill** | *(filled cell)* |
| Color RGB | no | `R, G, B`. Used when Color has no fill, and always for CSV. | `168, 174, 73` |
| Shared Access | no | `x` / `yes` / `true` / `1` / `shared` = the room opens directly onto its Conn. Primary room through a shared wall (no corridor door). | `x` |
| Room Width Min. | no | Minimum width, in ft | `5` |
| Aspect Ratio | no | Text. `None` = no constraint. | `1 to 1` |

## Rows

- **Room rows** have a valid Dept No and a name.
- **Department rows** have a whole-number Dept No and the department name (e.g. `7, Administration/Student Services`). They name departments and are not rooms. In Excel, department names are read from `ProjectSheet` instead.
- **Divider rows** (Dept No not a valid id, and name blank or `0`) and blank rows are skipped. They are counted in the import summary.

## Tips for the person filling it in

- Type ids as **text** in Excel (`'7.10`). A number like `7.10` is stored as `7.1`. The reader recovers it from the cell's number format, but text is safer (W004).
- One Dept No per row. Duplicates are errors (E001).
- In CSV, use `.` for decimals, and quote fields that contain commas (`"168, 174, 73"`).
- Units are feet and square feet.
