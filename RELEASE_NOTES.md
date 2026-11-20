<!-- 每次发布前更新本文件，Release 工作流会将其作为 GitHub Release 正文。 -->

## 更新内容

### Windows

- 修复浅色 / 深色主题切换不生效的问题，界面、卡片、文字和下拉框会完整切换。
- 调整两套主题的文字对比度，弱化文字在浅色和深色背景下都保持可读。
- 修复右上角刷新、复制链接、打开链接、关闭等按钮显示为方框的问题，图标不再依赖系统字体。
- Windows 可执行文件更名为 `Lumen-<版本>.exe`，方便区分版本。
- 继续提供自带运行时与依赖 .NET 8 桌面运行时两种压缩包。
- 两个包均不包含 `.pdb` 调试文件。

### Android

- 新增外观设置：跟随系统 / 浅色 / 深色，切换后立即生效并保存。
- 浅色模式使用独立的背景、卡片和文字配色，修正浅色下的文字对比度。

### 发布

- 固定 Android 签名证书保持不变，已安装用户可以直接覆盖升级。

## 下载

- **Windows x64（自带运行时）**：解压 `Lumen-Windows-x64-self-contained-*.zip`，直接运行 `Lumen-<版本>.exe`
- **Windows x64（需 .NET 8 桌面运行时）**：解压 `Lumen-Windows-x64-framework-dependent-*.zip`，安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 后运行
- **Android**：`Lumen-Android-*.apk` 使用固定 Release 证书签名，可直接覆盖升级；签名信息见 `ANDROID-CERTIFICATE.txt`

## Docker

镜像同时推送到 GHCR：

```bash
docker pull ghcr.io/2746267826/lumen:latest
```

详细部署说明见仓库 README。
