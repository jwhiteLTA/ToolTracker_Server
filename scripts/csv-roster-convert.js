// ==========================================
// Converts a "names on one line, emails on the next" roster dump into the CSV shape the
// bulk Personnel import expects (Badge ID,Username,Full Name,Email,Department,Role).
// ==========================================
// This is the shape a quick spreadsheet-column copy/paste tends to produce -- one line of
// comma-separated full names, one line of comma-separated emails, in the same order (the Nth
// name belongs to the Nth email). Badge ID, Department, and Role are intentionally left blank
// for every row -- that's what the import's own review screen is for, not this script.
//
// Usage: node scripts/csv-roster-convert.js input.txt output.csv
//
// Input: the first two non-blank lines of input.txt -- line 1 is the names, line 2 is the
// emails. Blank lines around them (or between them) are fine and ignored.

const fs = require('fs');

const [, , inputPath, outputPath] = process.argv;
if (!inputPath || !outputPath) {
    console.error('Usage: node scripts/csv-roster-convert.js <input.txt> <output.csv>');
    process.exit(1);
}

const raw = fs.readFileSync(inputPath, 'utf8');
const lines = raw.split(/\r?\n/).map(l => l.trim()).filter(l => l.length > 0);

if (lines.length < 2) {
    console.error('Expected at least two non-blank lines in the input: one of names, one of emails.');
    process.exit(1);
}

const names = lines[0].split(',').map(s => s.trim()).filter(Boolean);
const emails = lines[1].split(',').map(s => s.trim()).filter(Boolean);

if (names.length !== emails.length) {
    console.error(`Mismatch: ${names.length} name(s) but ${emails.length} email(s) -- these need to line up 1:1 by position. Fix the input and re-run.`);
    process.exit(1);
}

// Minimal CSV escaping -- only needed if a name/email ever contains a comma, quote, or newline,
// which isn't expected here but costs nothing to handle correctly.
const escapeCsv = (val) => /[",\n]/.test(val) ? `"${val.replace(/"/g, '""')}"` : val;

const header = 'Badge ID,Username,Full Name,Email,Department,Role';
const rows = names.map((name, i) => `,,${escapeCsv(name)},${escapeCsv(emails[i])},,`);

fs.writeFileSync(outputPath, [header, ...rows].join('\n') + '\n');
console.log(`Wrote ${rows.length} row(s) to ${outputPath}.`);
console.log('Badge ID, Department, and Role are blank -- upload this to the Personnel import and assign those in the review screen.');
