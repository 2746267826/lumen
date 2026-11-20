# Lumen · 流明互传

[![CI](https://github.com/2746267826/lumen/actions/workflows/ci.yml/badge.svg)](https://github.com/2746267826/lumen/actions/workflows/ci.yml)
[![Docker image](https://github.com/2746267826/lumen/actions/workflows/docker.yml/badge.svg)](https://github.com/2746267826/lumen/actions/workflows/docker.yml)
[![GitHub release](https://img.shields.io/github/v/release/2746267826/lumen?display_name=tag&sort=semver)](https://github.com/2746267826/lumen/releases)

Lumen 是一个面向 Windows 与 Android 的局域网优先设备互传工具。同一网络内自动发现并直连，跨网络时自动回退到自建服务器中转；所有传输都保留可审计的分块、速度、地址和事件记录。

客户端全部为原生实现：Windows 使用 **WPF (.NET 8)**，Android 使用 **Kotlin + Jetpack Compose**，没有 WebView。服务端是 **ASP.NET Core 8 + SQLite**，以 Linux Docker 容器部署。

## 下载与镜像

- Windows / Android 安装包：<https://github.com/2746267826/lumen/releases>
- Docker 镜像：`ghcr.io/2746267826/lumen:latest`
- 在线构建状态：<https://github.com/2746267826/lumen/actions>

Release 资产包含 Windows x64 自包含压缩包、固定证书签名的 Android APK、签名证书信息和 `SHA256SUMS.txt`。

## 功能

- 文件、图片、文字互传；支持多选文件/图片。
- 自动选路：UDP 组播发现 → HTTP 局域网直连；直连失败自动切换服务器中转。
- 设备绑定：登录同一账户的设备上线即出现，不需要每次在局域网内搜索。
- 完整状态：显示当前走局域网还是服务器中转，以及 IPv4/IPv6、地址、端口、延迟、在线状态。
- 传输记录：本地历史 + 服务端记录；详情包含文件大小、分块大小、分块数量、完成分块、传输字节、瞬时/平均速度、SHA-256、两端地址、事件时间线。
- 完整性校验：每个分块 SHA-256，接收端合并后再次校验整文件 SHA-256。
- 公开分享：文字或文件生成网页链接，可设置有效期、访问密码、下载次数上限；接收方无需安装客户端。
- 断点重试：分块上传失败自动重试，局域网失败自动回退中转。
- Android 系统分享入口：从相册、文件管理器或其他应用直接分享到 Lumen 并选择设备。
- Android 前台服务：登录后保持后台在线与接收，切到后台不会立即被系统回收。
- 原生体验：深色玻璃质感界面、动效、自适应布局。

## 架构

```text
┌──────────────── Windows (WPF) ────────────────┐
│  设置/历史 (JSON) · DPAPI 令牌保护              │
│  UDP 组播发现 · 本地 HTTP 分块接收服务          │
│  WebSocket 信令 · 自适应选路 · 分块传输协调器   │
└───────────────┬───────────────────────────────┘
                │ 同一局域网：HTTP 47655/tcp
                │ 跨网络：WebSocket + HTTPS 8080
┌───────────────▼───────────────────────────────┐
│  Linux Docker: ASP.NET Core 8 + SQLite        │
│  账户/设备绑定 · 在线状态 · 中转分块存储        │
│  公开分享页 · 清理任务 · WebSocket 信令         │
└───────────────┬───────────────────────────────┘
                │
┌───────────────▼───────────────────────────────┐
│  Android (Kotlin + Compose)                    │
│  SAF 文件选择 · Keystore 令牌 · 组播发现        │
│  NanoHTTPD 本地接收 · OkHttp WebSocket/分块     │
└───────────────────────────────────────────────┘
```

### 局域网直连

- 组播地址：`239.255.77.88:47654/udp`（IPv4；报文同时携带 IPv6 地址）。
- 每 3 秒广播一次设备信息，12 秒未收到视为离线。
- 接收端启动本地 HTTP 服务，默认端口 `47655`，被占用时在 `47655–47685` 内自动选择。
- 发送端探测 `/lumen/v1/ping` 后创建邀请；接收端接受后，按块 PUT 上传，最后合并并校验。
- 局域网失败（探测失败、邀请超时、分块中断）时自动改用服务器中转；对方明确拒绝时不会回退。

### 服务器中转

- 登录后建立 `GET /api/v1/ws?deviceId=...` WebSocket 长连接，服务器实时推送设备上下线和传输事件。
- 发送端创建传输 → 接收端接受 → 发送端逐块上传 → 服务端标记 ready → 接收端逐块下载 → 接收端确认完成。
- 中转只允许同一账户内的已绑定设备互相发送。
- 服务端保留传输记录与分块文件，默认 14 天后清理；分享文件按到期时间清理。

## 快速开始

### 1. 启动服务端

```bash
cp .env.example .env
# 编辑 .env，至少把 LUMEN_PUBLIC_URL 改成客户端可访问的域名或 IP
docker compose up -d --build
docker compose ps
curl http://127.0.0.1:8080/api/v1/health
```

也可以直接使用 CI 构建并推送到 GHCR 的镜像：

```bash
LUMEN_IMAGE=ghcr.io/2746267826/lumen:latest \
LUMEN_PUBLIC_URL=https://lumen.example.com \
docker compose -f deploy/docker-compose.ghcr.yml up -d
```

如果仓库或 GHCR 包是私有的，先执行 `echo "$GITHUB_TOKEN" | docker login ghcr.io -u <用户名> --password-stdin`。首次发布后可在 GitHub 的 Package 设置中把镜像改为 Public。

默认若 `.env` 未设置 `LUMEN_PUBLIC_URL`，分享链接会根据请求的 `Host` 自动生成。生产环境建议明确设置为外网地址。

数据保存在 Docker 命名卷 `lumen-data` 中：

```bash
# 备份
docker run --rm -v lumen-data:/data -v "$PWD":/backup alpine \
  tar czf /backup/lumen-data.tar.gz -C /data .

# 恢复
docker run --rm -v lumen-data:/data -v "$PWD":/backup alpine \
  tar xzf /backup/lumen-data.tar.gz -C /data
```

容器启动时以 root 修正数据卷所有权，然后通过 `setpriv` 降权到 UID/GID `10001` 运行。

### 2. HTTPS 反向代理

公网部署强烈建议只暴露 HTTPS。可直接使用 `deploy/Caddyfile`：

```bash
sudo cp deploy/Caddyfile /etc/caddy/Caddyfile
sudo systemctl reload caddy
```

或参考 `deploy/nginx.conf`。注意要允许大请求体、关闭请求缓冲并保持 WebSocket 升级头。

然后设置：

```env
LUMEN_PUBLIC_URL=https://lumen.example.com
```

### 3. Windows 客户端

需要 .NET 8 SDK（构建）或 Windows 10/11 x64（运行）。

```bash
# 框架依赖构建
dotnet build src/Lumen.Windows/Lumen.Windows.csproj -c Release

# 自包含单文件发布
./scripts/publish-windows.sh
```

程序位于 `artifacts/windows/Lumen.Windows.exe`。首次启动在设置页填写服务器地址，注册或登录后设备会自动绑定。

接收文件默认保存到 `文档\Lumen`，可在设置页修改。

### 4. Android 客户端

环境要求：JDK 17、Android SDK（compileSdk 35）。SDK 路径写入 `src/Lumen.Android/local.properties`：

```properties
sdk.dir=/path/to/android-sdk
```

构建 Debug APK：

```bash
cd src/Lumen.Android
./gradlew :app:assembleDebug
# 输出：app/build/outputs/apk/debug/app-debug.apk
```

安装到已连接的设备：

```bash
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

发布签名版时，复制 `src/Lumen.Android/keystore.properties.example` 为 `src/Lumen.Android/keystore.properties`，填写 keystore 路径和密码，再执行：

```bash
./scripts/build-android.sh
# 有签名配置：app/build/outputs/apk/release/app-release.apk
# 无签名配置：app/build/outputs/apk/release/app-release-unsigned.apk
```

接收文件保存到 `Android/data/dev.lumen.app/files/Download/Lumen`，可从传输详情直接打开。

## 使用

1. 在 Windows 和 Android 上登录同一账户。
2. 两端打开 Lumen，设备卡片会自动出现；同一 Wi-Fi 下还会显示“局域网直连”和 IPv4/IPv6 标签。
3. 点设备的“文件 / 图片 / 文字”发起传输。
4. 接收端在“传输”页接受邀请；同一账户可开启“自动接受”（只对已绑定设备生效）。
5. 在传输卡片或详情页查看链路、速度、分块和事件；完成后可直接打开文件或复制文字。
6. “分享”页可创建公开链接，设置有效期、密码和下载次数。

## 配置项

| 环境变量 | 默认值 | 说明 |
| --- | --- | --- |
| `Lumen__DataDir` | `/data` | SQLite、分块与分享文件目录 |
| `Lumen__PublicUrl` | 空 | 分享链接前缀；为空时使用请求 Host |
| `Lumen__AllowRegistration` | `true` | 是否允许注册 |
| `Lumen__SessionDays` | `90` | 登录会话有效期 |
| `Lumen__RetentionDays` | `14` | 已完成传输保留天数 |
| `Lumen__MaxShareBytes` | `21474836480` | 单个公开分享大小上限（20 GiB） |
| `Lumen__MaxTransferBytes` | `214748364800` | 单次设备传输大小上限（200 GiB） |
| `Lumen__ChunkMaxBytes` | `8388608` | 服务端允许的最大分块（8 MiB） |

客户端分块大小可在设置页选择 256 KiB / 1 MiB / 4 MiB；服务端上限 8 MiB。

## 网络端口

| 端口 | 协议 | 用途 |
| --- | --- | --- |
| `8080` | TCP/HTTP(S) | API、WebSocket、分享页 |
| `47654` | UDP | 局域网设备发现组播 |
| `47655–47685` | TCP | 客户端局域网直传（自动选择） |

服务端不需要开放 UDP 端口；局域网发现只在同一网段内使用。

## 安全说明

- 公网部署必须使用 HTTPS，否则账户令牌、文件内容和分享密码都会以明文经过网络。
- 服务器上的传输分块和分享文件没有额外应用层加密；请依赖磁盘权限、HTTPS 和受控主机。
- Android 令牌使用 Android Keystore 的 AES-GCM 密钥加密后保存；Windows 使用 DPAPI 保护。
- 局域网直传默认面向可信局域网；接受设备仍需确认，且使用每次传输随机生成的 upload token。
- 自动接受只对服务器账户中已绑定的设备生效，不会自动接受陌生局域网设备。
- 分享密码使用 PBKDF2 哈希保存，下载计数和有效期由服务端强制校验。

## 项目结构

```text
src/Lumen.Shared/     客户端与服务端共享协议、DTO、端口常量
src/Lumen.Server/     ASP.NET Core API、WebSocket、SQLite、分享页、Docker
src/Lumen.Windows/    WPF 客户端、发现、直连服务、传输协调器、界面
src/Lumen.Android/    Kotlin + Compose 客户端、SAF、组播发现、直连服务
deploy/               Caddy / Nginx 反向代理示例
scripts/              Windows 发布、Android 构建脚本
.github/workflows/    CI、Docker 多架构构建、Release 发布
docker-compose.yml    服务端部署
```

## 在线构建与发布

仓库包含三条 GitHub Actions 工作流：

| 工作流 | 触发条件 | 产物 |
| --- | --- | --- |
| `CI` | Push 到 `main`、Pull Request、手动触发 | 服务端构建与健康检查、Windows WPF 编译、Android Debug APK、Docker 镜像启动检查 |
| `Docker image` | Push 到 `main` 或 `v*` 标签 | `ghcr.io/2746267826/lumen` 多架构镜像，标签包含 `latest`、分支名、`sha-*`、语义化版本 |
| `Release` | 推送 `v*` 标签 | Windows 压缩包、固定证书签名的 Android APK、签名证书信息、校验和、GitHub Release |

### 发布新版本

```bash
git tag v1.0.0
git push origin v1.0.0
```

`v1.0.0` 会同时触发客户端 Release 和 Docker 版本镜像。Windows 程序集版本、Android `versionName` 自动取自标签，Android `versionCode` 使用 Actions 运行编号。

### Android 签名

本仓库已配置固定的 Release 签名密钥。`v1.0.1` 及之后的正式 APK 都使用同一张证书，因此可以直接覆盖升级安装。

正式签名证书 SHA-256：

```text
61:3F:4B:4C:C9:86:A7:79:7D:13:EA:60:48:8A:4A:2E:5B:36:0F:C2:6F:ED:08:B2:EC:99:AD:B4:CF:14:7F:CC
```

每次 Release 都会附带 `ANDROID-CERTIFICATE.txt`，可用 `apksigner verify --print-certs` 核对。

> 重要：不要删除或替换仓库中的签名 Secrets。更换证书后，已安装用户将无法覆盖升级，只能卸载重装。

签名由以下 GitHub Actions Secrets 提供：

| Secret | 说明 |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | keystore 文件的 Base64，例如 `base64 -w0 release.keystore` |
| `ANDROID_KEYSTORE_PASSWORD` | keystore 密码 |
| `ANDROID_KEY_ALIAS` | 密钥别名 |
| `ANDROID_KEY_PASSWORD` | 密钥密码 |

本地签名仍可使用 `src/Lumen.Android/keystore.properties`，格式见 `keystore.properties.example`。

如果 Secrets 缺失（例如 Fork 仓库），Release 会自动回退为 Debug APK + 明确标记的 `unsigned` APK。`v1.0.0` 的 Debug APK 使用 Android 临时 Debug 证书，无法升级到正式签名包；从 `v1.0.1` 起安装正式签名包即可持续覆盖升级。

Dependabot 每周检查 GitHub Actions、Gradle、NuGet 和 Docker 基础镜像更新。

## 已验证

- 服务端：双设备上线、传输创建/接受、3 分块上传、下载校验、WebSocket 进度、完成回执、详情事件、平均速度。
- 分享：密码校验、公开下载、下载计数递增、文件分享上传/下载、到期/撤销路径。
- Docker：镜像构建、数据卷权限、健康检查、真实 HTTP 健康响应。
- Android：真实模拟器启动、登录与设备绑定；局域网文字与 3 分块文件接收（SHA-256 一致）；服务器中转文字发送；服务器中转 3 分块文件接收（SHA-256 一致）；文字/文件分享创建、密码下载验证。
- Android 增强：设备卡片显示“服务器中转已连接”与 IPv4/IPv6 地址；系统分享文字入口弹出设备选择；前台服务通知与后台存活验证通过。
- Windows：`dotnet build` 与服务端互操作逻辑通过编译；当前验收环境为 Linux，WPF 界面运行需在 Windows 上做最终视觉验收。
- GitHub Actions：`CI` 的服务端、Windows、Android、Docker 四个 job 全部通过；`v1.0.0` Release 已产出 Windows 压缩包、Android Debug/unsigned APK 与 `SHA256SUMS.txt`。
- Android 签名：`v1.0.1` 正式包已用固定证书签名，`apksigner` 指纹与 keystore 一致；在真实模拟器上验证了 `1.0.1 (versionCode 3)` 覆盖升级到 `1.0.2 (versionCode 4)`。
- GHCR：`1.0.0`、`1.0`、`latest`、`sha-*` 标签均已推送，manifest 同时包含 `linux/amd64` 与 `linux/arm64`。

## 已知限制

- Android 使用 `dataSync` 类型前台服务；Android 15 对单次前台服务运行时长有系统级限制，超大文件建议保持应用可见。
- 局域网发现依赖组播，部分企业 Wi-Fi 会屏蔽组播；此时同网设备仍可通过服务器中转传输。
- 中转不提供应用层端到端加密，生产环境应使用 HTTPS 与可信服务器。
- 服务器不提供内网穿透或 TURN；跨公网依赖双方都能访问服务端。
- 反向代理必须正确转发 WebSocket 与 `X-Forwarded-*` 头。
