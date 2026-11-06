// Runs every runnable tutorial example in the sandbox interpreter and compares
// the output with the expected output shown in the lesson.
import { lessons } from "../src/tutorial/lessons.js";
import { runAqua } from "../src/tutorial/aqua.js";

let failed = 0;
for (const lesson of lessons) {
  if (lesson.runnable === false) continue;
  for (const lang of ["zh", "en"]) {
    const { output, error } = runAqua(lesson.code[lang]);
    let got = output;
    if (error) got += (got ? "\n" : "") + (lang === "zh" ? `⚠ 第 ${error.line} 行：` : `⚠ Line ${error.line}: `) + error.message;
    const want = lesson.output[lang];
    if (got !== want || !!error !== !!lesson.expectError) {
      failed++;
      console.log(`✗ ${lesson.id} (${lang})\n--- expected\n${want}\n--- got\n${got}\n`);
    } else console.log(`✓ ${lesson.id} (${lang})`);
  }
}
process.exit(failed ? 1 : 0);
