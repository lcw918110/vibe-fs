import fs from 'node:fs'
import path from 'node:path'
import { execFile } from 'node:child_process'

function outsideGit(directory) {
  if (directory.split(/[\\/]+/).some(part => part.toLowerCase() === '.git')) {
    throw new Error('Application state must not be read or written inside .git')
  }
  return directory
}

function execGitWorktreeList(directory) {
  return new Promise((resolve) => {
    execFile('git', ['-C', directory, 'worktree', 'list', '--porcelain', '-z'], {
      encoding: 'utf8', env: { ...process.env, LC_ALL: 'C', GIT_OPTIONAL_LOCKS: '0' },
    }, (error, stdout, stderr) => {
      resolve({
        error: error && !error.code && typeof error.status !== 'number' ? error : null,
        status: error ? (typeof error.code === 'number' ? error.code : (typeof error.status === 'number' ? error.status : null)) : 0,
        stdout: stdout || '',
        stderr: stderr || '',
      })
    })
  })
}

// 脚本不加载应用产物；只查询 Git 拓扑，与运行时采用同一个目录合同。
export async function stateRoot(workspace) {
  if (typeof workspace !== 'string' || !workspace.trim()) throw new Error('A workspace directory is required')
  const directory = outsideGit(fs.realpathSync(outsideGit(path.resolve(workspace))))
  if (!fs.statSync(directory).isDirectory()) throw new Error('The workspace must be a directory')
  const result = await execGitWorktreeList(directory)
  if (result.error) throw result.error
  let primary = directory
  if (result.status === 0) {
    const first = result.stdout.split('\0\0', 1)[0].split('\0')
    if (first.includes('bare')) throw new Error('Application state requires a working tree, not a bare repository')
    if (!first[0].startsWith('worktree ')) throw new Error('Git did not identify the primary working tree')
    primary = outsideGit(fs.realpathSync(first[0].slice('worktree '.length)))
  } else if (result.status !== 128 || !result.stderr.startsWith('fatal: not a git repository')) {
    throw new Error(`Cannot locate application state: ${result.stderr.trim() || `git exited ${result.status}`}`)
  }
  const target = path.join(primary, '.Herdchestra')
  const entry = fs.lstatSync(target, { throwIfNoEntry: false })
  if (entry && (entry.isSymbolicLink() || !entry.isDirectory())) {
    throw new Error('.Herdchestra must be a real directory, not a link or file')
  }
  return target
}

export async function initializeStateRoot(workspace) {
  const target = await stateRoot(workspace)
  fs.mkdirSync(target, { recursive: true })
  const ignore = path.join(target, '.gitignore')
  try {
    fs.writeFileSync(ignore, '*\n', { flag: 'wx', encoding: 'utf8' })
  } catch (error) {
    if (error.code !== 'EEXIST') throw error
    const entry = fs.lstatSync(ignore)
    if (!entry.isFile() || entry.isSymbolicLink() || fs.readFileSync(ignore, 'utf8').trim() !== '*') {
      throw new Error('.Herdchestra/.gitignore must be a regular file excluding all runtime data')
    }
  }
  return target
}
