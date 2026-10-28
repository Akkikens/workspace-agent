// Default include/exclude rules for workspace indexing. Pure: no `vscode` import.

export const DEFAULT_MAX_FILES = 2000;

export const DEFAULT_EXCLUDE_DIRS: readonly string[] = [
  'node_modules', '.git', 'bin', 'obj', 'dist', 'out', '.angular', '.vscode-test', '.next', 'build',
  'coverage', '__pycache__', '.venv', 'venv', 'target', '.idea', '.vs',
];

export const DEFAULT_EXCLUDE_FILES: readonly string[] = [
  'package-lock.json', 'yarn.lock', 'pnpm-lock.yaml', 'packages.lock.json', 'poetry.lock', 'Pipfile.lock',
  'Cargo.lock', 'composer.lock', 'Gemfile.lock', 'go.sum',
];

export const DEFAULT_EXCLUDE_EXTENSIONS: readonly string[] = [
  // images
  'png', 'jpg', 'jpeg', 'gif', 'bmp', 'ico', 'svg', 'webp', 'tiff', 'psd',
  // binaries, archives, media, fonts
  'pdf', 'zip', 'gz', 'tgz', 'tar', '7z', 'exe', 'dll', 'so', 'dylib', 'pdb', 'wasm', 'jar', 'class',
  'pyc', 'woff', 'woff2', 'ttf', 'otf', 'eot', 'mp3', 'mp4', 'mov', 'wav', 'avi', 'sqlite', 'db', 'vsix',
  // generated / noisy text
  'lock', 'map', 'min.js', 'min.css', 'snap',
];

/** A single glob suitable for `vscode.workspace.findFiles(include, exclude)`. */
export function buildExcludeGlob(): string {
  const patterns = [
    ...DEFAULT_EXCLUDE_DIRS.map((d) => `**/${d}/**`),
    ...DEFAULT_EXCLUDE_FILES.map((f) => `**/${f}`),
    ...DEFAULT_EXCLUDE_EXTENSIONS.map((e) => `**/*.${e}`),
  ];
  return `{${patterns.join(',')}}`;
}

/** Same rules as `buildExcludeGlob`, for paths coming from file watcher events. */
export function isIndexablePath(relativePath: string): boolean {
  const segments = relativePath.split(/[\\/]+/).filter((s) => s.length > 0);
  if (segments.length === 0) return false;
  const fileName = segments[segments.length - 1];
  if (segments.slice(0, -1).some((s) => DEFAULT_EXCLUDE_DIRS.includes(s))) return false;
  if (DEFAULT_EXCLUDE_FILES.includes(fileName)) return false;
  const lower = fileName.toLowerCase();
  return !DEFAULT_EXCLUDE_EXTENSIONS.some((ext) => lower.endsWith(`.${ext}`));
}

export function clampMaxFiles(value: unknown): number {
  const n = typeof value === 'number' && Number.isFinite(value) ? Math.floor(value) : DEFAULT_MAX_FILES;
  return Math.min(Math.max(n, 1), 50_000);
}
