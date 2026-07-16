# v2rayN

[English](README.md) | [Русский](README-RU.md) | **中文**

### 适用于 Windows、Linux 和 macOS 的 GUI 客户端。支持 [Xray](https://github.com/XTLS/Xray-core)、[sing-box](https://github.com/SagerNet/sing-box) 及[其他内核](https://github.com/2dust/v2rayN/wiki/List-of-supported-cores)

[![CodeFactor](https://www.codefactor.io/repository/github/h1w/v2rayn-ru/badge)](https://www.codefactor.io/repository/github/h1w/v2rayn-ru)
[![Release](https://img.shields.io/github/v/release/h1w/v2rayN-RU?logo=github&label=Release)](https://github.com/h1w/v2rayN-RU/releases)
[![Downloads](https://img.shields.io/github/downloads/h1w/v2rayN-RU/latest/total?logo=github&label=Downloads)](https://github.com/h1w/v2rayN-RU/releases)
[![Telegram](https://img.shields.io/badge/Telegram-Chat-26A5E4?logo=telegram)](https://t.me/v2rayn)
 
[![Windows](https://img.shields.io/badge/Windows-supported-0078D6?logo=windows)](https://github.com/h1w/v2rayN-RU) 
[![Linux](https://img.shields.io/badge/Linux-supported-FCC624?logo=linux&logoColor=000)](https://github.com/h1w/v2rayN-RU) 
[![macOS](https://img.shields.io/badge/macOS-supported-000000?logo=apple)](https://github.com/h1w/v2rayN-RU) 
[![GPG Signed](https://img.shields.io/badge/GPG-signed-4B32C3?logo=gnuprivacyguard)](https://github.com/h1w/v2rayN-RU)


---

## 关于此分支

**v2rayN-RU** 是 [v2rayN](https://github.com/2dust/v2rayN) 的一个分支，新增功能：

- **客户端 HWID 支持** — 更新订阅时，客户端会发送与 Happ 兼容的 HWID 请求头（`x-hwid`、`x-device-os`、`x-ver-os`、`x-device-locale`），兼容 Remnawave Panel v2.9.0+。这样即可使用需要按设备硬件标识进行授权的订阅。
- **完整的 `.json` 配置支持** — 直接导入并运行完整的自定义 `.json` 内核配置。

---

## 下载

在这里下载最新版本：

[https://github.com/h1w/v2rayN-RU/releases](https://github.com/h1w/v2rayN-RU/releases)


> [!TIP]
> v2rayN 是电脑版，手机版请访问 v2rayNG
>
> https://github.com/2dust/v2rayNG

---

## 使用文档

请阅读 Wiki 获取使用说明和配置教程。

[https://github.com/2dust/v2rayN/wiki](https://github.com/2dust/v2rayN/wiki)

---

## 支持平台

| 平台 | x64 | x86 | arm64 | riscv64 | loong64 |
| --- | --- | --- | --- | --- | --- |
| Windows | ✅ | ✅ | ✅ | - | - |
| Linux | ✅ | - | ✅ | ✅ | ✅ |
| macOS | ✅ | - | ✅ | - | - |

---

## GPG 签名校验

发布文件已使用 GPG 签名，可用于校验文件真实性与完整性，预防镜像站、运营商或 CDN 劫持。

### 公钥指纹

```text
ECF0 C3FB E838 19F6 6D5D
0989 C946 B144 9B53 7603
```

---

## 社区

Telegram 群组：

[https://t.me/v2rayN](https://t.me/v2rayN)

Telegram 频道：

[https://t.me/github_2dust](https://t.me/github_2dust)
