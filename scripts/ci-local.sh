#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IMAGE_NAME="wanxiangshu-ci-local:latest"
WORK_VOLUME="wanxiangshu-ci-workspace"

echo "============================================================"
echo "🚀 [CI 1:1 本地全真模拟] 正在启动 GitHub Ubuntu 容器镜像..."
echo "============================================================"

# 镜像不存在，或 Dockerfile.ci 比镜像新时重建。
# 只判断「镜像存在」会让 Dockerfile 的改动（例如补装 ripgrep）永远进不去，
# 容器里缺工具时表现为一堆与本次改动无关的测试失败。
#
# 用 Node Date.parse 统一解析 Docker `{{.Created}}` 的 ISO-8601（`2026-11-05T12:34:56.123Z`）
# 与 Go 空格格式（`2026-11-05 12:34:56.123 +0000 UTC`），避免 macOS BSD date 与 Linux GNU date 选项互不兼容。
IMAGE_STALE=0
if docker image inspect "${IMAGE_NAME}" >/dev/null 2>&1; then
    IMAGE_CREATED="$(docker image inspect "${IMAGE_NAME}" --format '{{.Created}}' 2>/dev/null || true)"
    IMAGE_EPOCH="$(node -e '
        const raw = (process.argv[1] || "").trim().replace(/ \+\d{4} UTC$/, "Z");
        const ms = Date.parse(raw);
        if (Number.isFinite(ms)) process.stdout.write(String(Math.floor(ms / 1000)));
    ' "${IMAGE_CREATED}" 2>/dev/null || true)"
    DOCKERFILE_EPOCH="$(stat -f %m "${ROOT_DIR}/Dockerfile.ci" 2>/dev/null \
        || stat -c %Y "${ROOT_DIR}/Dockerfile.ci" 2>/dev/null \
        || echo '')"
    # 任一解析失败就 fail-open 到重建：宁可多建一次，不可让过期镜像蒙混过关。
    if [ -z "${IMAGE_EPOCH}" ] || [ -z "${DOCKERFILE_EPOCH}" ]; then
        IMAGE_STALE=1
    elif [ "${DOCKERFILE_EPOCH}" -gt "${IMAGE_EPOCH}" ]; then
        IMAGE_STALE=1
    fi
else
    IMAGE_STALE=1
fi

if [ "${IMAGE_STALE}" = "1" ]; then
    echo "📦 镜像 ${IMAGE_NAME} 缺失或已落后于 Dockerfile.ci，正在重建..."
    docker build -t "${IMAGE_NAME}" -f "${ROOT_DIR}/Dockerfile.ci" "${ROOT_DIR}"
fi

echo "🧪 正在进入纯净 Linux 容器执行 GitHub 完整 CI 门禁..."
echo "   步骤 1: 把工作树复制进容器内的 ext4 卷"
echo "   步骤 2: npm ci"
echo "   步骤 3: dotnet tool restore"
echo "   步骤 4: npm run verify:release (全部 7 级质量门禁)"
echo "------------------------------------------------------------"

# 为什么复制而不是挂载：
#
# Docker Desktop 在 macOS 上用 virtiofs 提供绑定挂载，它不满足构建对文件系统
# 的三条要求，而这三条都是硬性的：
#
#   1. 权限位要可信。构建把 NuGet 缓存里的 fable-library-js 拷进 .fable-build，
#      virtiofs 会把文件建成 0200，Fable 随后读它即 EACCES，构建第一步就死。
#   2. 目录要能被整体删除。构建收尾把 dist 整个 rmdir 再 rename 回来；若 dist
#      是挂载点，rmdir 直接 EBUSY。
#   3. 同一文件系统。dist 与 .fable-build 之间要 rename，跨设备即 EXDEV。
#
# 把工作树复制进命名卷（容器 VM 内的 ext4）一次满足三条：权限位是真的、目录
# 可以删、rename 不跨设备。宿主工作树全程只读，连 node_modules 都不会被容器
# 写坏——之前容器把 Linux 的 184MB ELF 写进宿主 node_modules，macOS 之后拿到
# 一堆执行不了的二进制。
#
# 构建以 uid 1000 跑（Dockerfile.ci 的 USER ubuntu，对齐 GitHub Actions）。
# 有几项测试靠文件权限注入故障——chmod 000 的不可读目录、chmod 444 的不可写
# 事件日志。root 无视权限位，故障注入静默失效，测试变成假红。
# 若当前目录是 linked git worktree，.git 文件指向宿主机 common gitdir 的绝对路径，
# 须将该路径按原绝对路径只读挂载进容器，避免容器内 git 命令报 not a git repository。
EXTRA_GIT_MOUNTS=()
GIT_COMMON_DIR="$(cd "${ROOT_DIR}" && git rev-parse --path-format=absolute --git-common-dir 2>/dev/null || true)"
if [ -n "${GIT_COMMON_DIR}" ] && [ "${GIT_COMMON_DIR}" != "${ROOT_DIR}/.git" ] && [ -d "${GIT_COMMON_DIR}" ]; then
    EXTRA_GIT_MOUNTS+=(-v "${GIT_COMMON_DIR}:${GIT_COMMON_DIR}:ro")
fi

docker run --rm \
    --init \
    --memory=6g \
    --user root \
    -v "${ROOT_DIR}:/src:ro" \
    "${EXTRA_GIT_MOUNTS[@]}" \
    -v "${ROOT_DIR}/.fable-build/verify-logs:/logs" \
    --mount type=volume,source="${WORK_VOLUME}",target=/workspace \
    -w /workspace \
    -e WXS_ACCEPT_TODO=1 \
    -e NODE_TEST_CONCURRENCY=2 \
    -e UNIT_VERDICT_SILENCE_MS=30000 \
    -e SUITE_BACKSTOP_MS=900000 \
    "${IMAGE_NAME}" \
    /bin/bash -c '
        set -e
        # 卷每次从空开始：上一轮的 dist 与依赖会让构建走缓存或撞上残留锁。
        find /workspace -mindepth 1 -maxdepth 1 -exec rm -rf {} +
        # 排除主仓的 .git 与构建产物，但保留子模块 wanxiangli/.git：
        #   distribution-011 会核对子模块 HEAD 与主仓 gitlink 是否一致，
        #   少了子模块的 .git 就退化成读主仓版本号，报 checkout 不匹配。
        tar -C /src --exclude=./.git --exclude=./node_modules --exclude=./dist \
            --exclude=./.fable-build --exclude=./artifacts \
            --exclude=./wanxiangli/node_modules --exclude=./wanxiangli/dist \
            --exclude=./wanxiangli/.fable-build --exclude=./wanxiangli/.build-service \
            -cf - . \
            | tar -C /workspace -xf -
        cp -R /src/.git /workspace/.git
        chown -R ubuntu:ubuntu /workspace
        runuser -u ubuntu -- git config --global --add safe.directory /workspace
        exec runuser -u ubuntu -- /bin/bash -c "
            cd /workspace
            npm ci
            dotnet tool restore
            npm run verify:release
        "
    ' || {
        # 失败时把日志带回宿主，否则诊断材料全留在卷里。
        mkdir -p "${ROOT_DIR}/.fable-build/verify-logs"
        docker run --rm --user root \
            --mount type=volume,source="${WORK_VOLUME}",target=/workspace \
            -v "${ROOT_DIR}/.fable-build/verify-logs:/logs" \
            "${IMAGE_NAME}" \
            /bin/bash -c 'cp -R /workspace/.fable-build/verify-logs/. /logs/ 2>/dev/null || true' || true
        echo "❌ [CI 1:1 本地全真模拟] 容器环境验证未通过。"
        echo "   日志已回传到 ${ROOT_DIR}/.fable-build/verify-logs/"
        exit 1
    }

echo "------------------------------------------------------------"
echo "🎉 [CI 1:1 本地全真模拟] 容器环境 7 级门禁全部通过！"
echo "============================================================"
