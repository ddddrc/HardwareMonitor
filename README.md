# HardwareMonitor

A high-performance, lightweight tool designed to show detailed hardware information and continuously monitor real-time system/hardware usage for Android integration and desktop management.

---

## 📖 Introduction

**HardwareMonitor** is built to bridge hardware diagnostic metrics and external system displays (such as Android devices used as secondary hardware monitoring screens). It provides real-time telemetry including CPU temperatures, GPU loads, memory consumption, disk I/O, and networking metrics.

---

## ✨ Features

- ⚡ **Real-time Monitoring**: High-frequency retrieval of core system statistics (CPU, GPU, RAM, Storage, Network).
- 📱 **Android Display Integration**: Stream or send hardware metrics directly to Android devices for desktop dashboard setups.
- 🚀 **Modern .NET Stack**: High throughput and low memory footprint powered by .NET 10.
- 🛠️ **Deep Hardware Access**: Leverages low-level sensor access for reliable temperature and usage telemetry.

---

## 🛠️ Built With

The project is developed using modern Microsoft C# tooling:

- **Framework**: [.NET 10.0](https://dotnet.microsoft.com/)
- **Language**: C#

### 📦 Key Dependencies / NuGet Packages

- [LibreHardwareMonitorLib](https://www.nuget.org/packages/LibreHardwareMonitorLib/) - Provides robust, low-level sensor access across a wide range of motherboards, CPUs, graphics cards, and storage devices.
- [System.Management](https://www.nuget.org/packages/System.Management/) - Provides access to Windows Management Instrumentation (WMI) infrastructure for system and hardware querying.

---

## 🚀 Quick Start

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or higher.
- Administrator privileges on Windows (required by `LibreHardwareMonitorLib` for low-level driver and hardware sensor reads).

### Installation & Build

1. Clone the repository:
   ```bash
   git clone [https://github.com/ddddrc/HardwareMonitor.git](https://github.com/your-username/HardwareMonitor.git)
   cd HardwareMonitor
