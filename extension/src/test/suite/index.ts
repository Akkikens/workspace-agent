// Mocha entry point loaded by the extension host (see runTest.ts).

import * as fs from 'fs';
import * as path from 'path';
import Mocha from 'mocha';

export function run(): Promise<void> {
  const mocha = new Mocha({ ui: 'bdd', color: true, timeout: 60_000 });
  for (const file of fs.readdirSync(__dirname)) {
    if (file.endsWith('.test.js')) mocha.addFile(path.join(__dirname, file));
  }
  return new Promise((resolve, reject) => {
    mocha.run((failures) => (failures > 0 ? reject(new Error(`${failures} integration test(s) failed`)) : resolve()));
  });
}
