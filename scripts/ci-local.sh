#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IMAGE_NAME="wanxiangshu-ci-local:latest"

echo "============================================================"
echo "🚀 [CI 1:1 本地全真模拟] 正在启动 GitHub Ubuntu 容器镜像..."
echo "============================================================"

# 如果本地不存在该镜像，执行首次构建
if ! docker image inspect "${IMAGE_NAME}" >/dev/null 2>&1; then
    echo "📦 镜像 ${IMAGE_NAME} 不存在，正在基于 Dockerfile.ci 进行全真环境构建..."
    docker build -t "${IMAGE_NAME}" -f "${ROOT_DIR}/Dockerfile.ci" "${ROOT_DIR}"
fi

echo "🧪 正在进入纯净 Linux 容器执行 GitHub 完整 CI 门禁..."
echo "   步骤 1: npm ci"
echo "   步骤 2: dotnet tool restore"
echo "   步骤 3: npm run verify:release (全部 7 级质量门禁)"
echo "------------------------------------------------------------"

docker run --rm \
    -v "${ROOT_DIR}:/workspace" \
    -w /workspace \
    -e WXS_ACCEPT_TODO=1 \
    -e NODE_TEST_CONCURRENCY=2 \
    -e UNIT_VERDICT_SILENCE_MS=30000 \
    "${IMAGE_NAME}" \
    /bin/bash -c "npm ci && dotnet tool restore && npm run verify:release"

echo "------------------------------------------------------------"
echo "🎉 [CI 1:1 本地全真模拟] 容器环境 7 级门禁全部通过！"
echo "============================================================"
