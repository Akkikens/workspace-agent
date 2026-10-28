// Downloads VS Code (cached in .vscode-test/), then runs the suite in an extension host
// with the fixture workspace open.

import * as path from 'path';
import { runTests } from '@vscode/test-electron';

async function main(): Promise<void> {
  // Compiled to out/test/runTest.js, so the extension root is two levels up.
  const extensionDevelopmentPath = path.resolve(__dirname, '../../');
  const extensionTestsPath = path.resolve(__dirname, './suite/index');
  // The fixture is read from the source tree; tsc does not copy it into out/.
  const workspace = path.resolve(extensionDevelopmentPath, 'src/test/fixtures/sample-workspace');

  await runTests({
    version: process.env.VSCODE_TEST_VERSION || 'stable',
    extensionDevelopmentPath,
    extensionTestsPath,
    launchArgs: [workspace, '--disable-extensions', '--disable-workspace-trust', '--skip-welcome', '--skip-release-notes'],
  });
}

main().catch((err) => {
  console.error('Integration tests failed:', err);
  process.exit(1);
});
