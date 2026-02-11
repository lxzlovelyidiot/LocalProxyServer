# LocalProxyServer

`LocalProxyServer` 是一个本地代理服务器，支持在同一端口自动识别并处理 HTTP 与 HTTPS 代理请求，适用于开发调试与本地环境代理转发。

## 功能简介

- HTTP/HTTPS 代理（同端口自动识别 TLS）
- 支持上游代理：HTTP 与 SOCKS5
- 自签名 CA 证书自动生成与安装（HTTPS 模式）
- 结构化日志输出，便于排查问题
 - 注册 CA 证书到系统信任库需要管理员权限

## 快速开始

详细使用方法与环境配置请参考 `./documents/README.md`。
