# PC Control — Tài liệu mô tả dự án

> **Trạng thái:** Product concept / Technical direction — Integrated v3  
> **Thay đổi so với v2:** xem mục 0 (Changelog)  
> **Tên hiện tại:** PC Control *(working title — có thể đổi sau)*  
> **Nền tảng ưu tiên:** Windows 11  
> **Định vị ngắn:** Một lớp điều khiển và quản lý trạng thái thống nhất cho toàn bộ PC — từ Windows, BIOS/UEFI, driver, phần cứng, thiết bị ngoại vi đến cấu hình ứng dụng — có khả năng hiểu dependency, tính difference plan, áp dụng thay đổi theo transaction, kiểm chứng kết quả, phát hiện drift và bảo toàn cách chiếc PC hoạt động qua thời gian.

---

## 0. Thay đổi trong v3 (Changelog)

Bản v3 không đổi định vị, kiến trúc lõi hay vertical slice. Nó sửa một giả định sai, bổ sung các phần còn thiếu về trải nghiệm người dùng và chuẩn hóa chiến lược hỗ trợ nhiều dòng máy để dự án đi được lâu dài.

| Nhóm | Thay đổi |
|---|---|
| **BIOS/UEFI** | Tách rõ hai khả năng độc lập: **đọc trạng thái firmware** (khả thi trên gần như mọi máy qua Windows) và **ghi setting firmware** (chỉ khả thi khi vendor có API chính thức). Guided trở thành đường mặc định cho desktop board; Auto là nâng cấp theo adapter vendor. Mục 8.3 viết lại; thêm mục 8.3.1–8.3.5. |
| **Chiến lược dòng máy** | Thêm mục 18.4 *Hardware Support Tiers*: thứ tự hỗ trợ Dell → Lenovo → HP → laptop tiêu dùng → desktop board (Guided), và tiêu chí để một dòng máy được nâng tier. |
| **UX cho người dùng phổ thông** | Thêm mục 21.4–21.12: Simple/Advanced mode, first-run, ngôn ngữ đời thường cho finding, header “chi phí của plan”, Restart scheduling, panel *Recently Changed / Undo*, Standard User mode, Portable/Support mode, Accessibility. |
| **Đa ngôn ngữ** | Thêm mục 21.11 *Localization*: kiến trúc i18n phải có từ v0.1 (string externalization, locale-aware evidence, alias đa ngữ cho search), thứ tự ngôn ngữ, và nguyên tắc dịch thuật ngữ kỹ thuật. |
| **An toàn thực tế** | Thêm BitLocker preflight bắt buộc cho mọi thay đổi firmware/boot (mục 10.3), rủi ro 27.12–27.13. |
| **Drift UX** | Mục 13.4 bổ sung: im lặng mặc định, digest, *Accept as new normal*, snooze theo item. |
| **MVP v0.1** | Mục 23.1.C viết lại; thêm 23.1.H (Display refresh-rate fix với auto-revert) để wow moment chạm được gamer và người thường; thêm 23.1.I (i18n foundation). |
| **Roadmap/Quyết định** | Quyết định 7 sửa lại; thêm quyết định 18–23; thêm bước 6b (UX one-pager + hallway test) trong mục 31. |

---

## 1. Tóm tắt dự án

PC Control là một ứng dụng desktop giúp người dùng xem, cấu hình, sửa lỗi, bảo trì và di chuyển trạng thái sử dụng của máy tính trong một giao diện duy nhất.

Hiện tại, các cài đặt quan trọng của một chiếc PC bị phân tán ở rất nhiều nơi:

- Windows Settings và Control Panel;
- BIOS/UEFI;
- Device Manager, Services, Task Scheduler và Registry;
- NVIDIA App, AMD Software, Intel Graphics;
- ứng dụng của nhà sản xuất mainboard/laptop;
- phần mềm riêng của màn hình, chuột, bàn phím, webcam, DAC và RGB;
- PowerShell, DISM, `optionalfeatures`, `wsl`, `powercfg` và nhiều công cụ dòng lệnh khác.

Người dùng thường phải biết trước tên kỹ thuật của tính năng, vị trí của setting và hậu quả của từng thay đổi. Khi gặp lỗi, họ phải ghép nhiều bài hướng dẫn khác nhau và khó biết bước nào đã thực sự thành công.

PC Control giải quyết vấn đề đó bằng cách đưa các capability quan trọng của PC về cùng một mô hình:

```text
Discover → Understand → Compare → Plan → Stage → Apply → Restart → Verify → Observe → Restore
```

Ở tầng sâu hơn, sản phẩm không chỉ là một giao diện gom setting. Nó duy trì một mô hình trạng thái của chiếc PC, hiểu quan hệ giữa phần cứng, firmware, Windows, driver và ứng dụng; từ đó có thể tính ra **difference plan** giữa trạng thái hiện tại và trạng thái người dùng mong muốn.

```text
Current PC State
      ↓
Desired State / Outcome
      ↓
PC Capability Graph
      ↓
Difference Plan
      ↓
Transactional Execution
      ↓
Verification + History + Drift Detection
```

Ứng dụng **không được định vị là công cụ “boost PC một nút”**, không âm thầm sửa hàng chục registry key và không hứa hẹn tăng hiệu năng thiếu căn cứ. Giá trị cốt lõi là sự rõ ràng, an toàn, nhất quán và khả năng kiểm chứng.

---

## 2. Tầm nhìn sản phẩm

### 2.1 Tầm nhìn

Trở thành lớp quản lý chung cho máy tính cá nhân, nơi người dùng có thể:

- biết máy đang có phần cứng và khả năng gì;
- phát hiện những cấu hình chưa hợp lý;
- thay đổi setting mà không phải nhớ nó nằm ở đâu;
- biết chính xác ứng dụng chuẩn bị làm gì;
- phục hồi khi một thay đổi hoặc bản cập nhật gây lỗi;
- giữ lại cách chiếc PC hoạt động khi cài lại Windows hoặc chuyển sang máy mới;
- biết máy đã lệch khỏi trạng thái ổn định hoặc trạng thái mong muốn ở đâu;
- biết trước một sự cố có thể liên quan đến thay đổi nào vừa xảy ra;
- mô tả một outcome như “Docker ready”, “Gaming ready” hoặc “Meeting ready” và để hệ thống tính các thay đổi thực sự cần thiết cho đúng chiếc máy đó.

### 2.2 Lời hứa sản phẩm

> **PC Control hiểu chiếc PC đang ở trạng thái nào, người dùng muốn nó ở trạng thái nào, cần thay đổi gì để đi từ A đến B, và liệu kết quả sau cùng có thực sự đạt hay không.**

### 2.3 Elevator pitch

> PC Control là lớp điều khiển trạng thái cho máy tính cá nhân: hiểu phần cứng và dependency của máy, tính kế hoạch thay đổi, áp dụng an toàn qua cả restart, kiểm chứng kết quả, phát hiện drift và mang cách chiếc PC hoạt động sang lần cài mới hoặc máy mới.

---

## 3. Bài toán cần giải quyết

### 3.1 Cài đặt bị phân mảnh

Một workflow đơn giản như chuẩn bị máy cho Docker có thể liên quan đến:

- CPU virtualization trong BIOS;
- Virtual Machine Platform;
- WSL 2;
- Windows Hypervisor Platform;
- WSL kernel và distro;
- restart và kiểm tra lại sau khi boot.

Không có một giao diện mặc định nào thể hiện trọn vẹn dependency chain này.

### 3.2 Người dùng không biết trạng thái thực tế của phần cứng

Ví dụ:

- RAM được quảng cáo 6000 MT/s nhưng đang chạy 4800 MT/s;
- màn hình hỗ trợ 165 Hz nhưng Windows đang đặt 60 Hz;
- SSD USB 10 Gbps đang cắm vào cổng chỉ chạy 5 Gbps;
- laptop cần sạc 100 W nhưng bộ sạc hiện tại chỉ cấp 65 W;
- Resizable BAR được phần cứng hỗ trợ nhưng chưa được bật đầy đủ.

### 3.3 Thay đổi hệ thống thiếu an toàn và khó hoàn tác

Các tool “optimizer” thường chỉ có một nút Apply, trong khi người dùng không biết:

- setting nào sẽ bị sửa;
- nguồn khuyến nghị là gì;
- có cần restart không;
- setting có ảnh hưởng đến app khác không;
- khi lỗi thì quay lại bằng cách nào.

### 3.4 Cập nhật và sửa lỗi không có lịch sử rõ ràng

Sau khi Windows, BIOS hoặc driver được cập nhật, lỗi có thể xuất hiện muộn. Người dùng khó liên hệ crash hiện tại với thay đổi trước đó và khó quay về trạng thái ổn định gần nhất.

### 3.5 Cài lại hoặc đổi PC làm mất “cách chiếc máy hoạt động”

File cá nhân có thể được đồng bộ, nhưng cấu hình ứng dụng, profile thiết bị, biến môi trường, WSL, game settings và hàng loạt lựa chọn hệ thống vẫn phải dựng lại thủ công.

---


### 3.6 Máy bị “drift” sau update nhưng người dùng không biết

Một chiếc PC có thể đang ở trạng thái đúng hôm nay nhưng thay đổi sau:

- Windows Update;
- driver GPU/chipset;
- BIOS update hoặc BIOS reset;
- cắm/rút màn hình, dock hoặc audio device;
- reinstall ứng dụng;
- OEM utility tự đổi profile;
- Windows tự chọn lại default device.

Ví dụ:

```text
Known-good state
Monitor refresh rate: 180 Hz
Resizable BAR: Enabled
Default audio: HyperX Headset

Current state
Monitor refresh rate: 60 Hz
Resizable BAR: Disabled
Default audio: Realtek
```

Các công cụ hiện tại thường cho user xem trạng thái hiện tại, nhưng không trả lời tốt câu hỏi: **“Chiếc PC của tôi đã lệch khỏi cấu hình mà tôi muốn ở đâu?”**

### 3.7 Khi máy bắt đầu lỗi, người dùng không biết “trước đó đã thay đổi gì”

Crash, FPS drop, mất audio hoặc lỗi Docker có thể xuất hiện sau một update hoặc thay đổi setting. Dữ liệu liên quan hiện nằm rải rác trong:

- Windows Update history;
- driver installer;
- Event Viewer;
- Reliability Monitor;
- firmware version;
- app logs;
- lịch sử setting riêng của từng utility.

Người dùng cần một **PC Change Timeline** hợp nhất các thay đổi có ý nghĩa để trả lời:

> Vấn đề bắt đầu từ khi nào, và ngay trước đó máy đã thay đổi những gì?

### 3.8 Hardware có capability nhưng toàn bộ đường truyền không đạt

Thông số danh nghĩa của một thiết bị không đảm bảo người dùng đang nhận được hiệu năng tương ứng.

Ví dụ:

```text
External SSD rated: 20 Gbps
Current link: 5 Gbps

Potential bottleneck:
SSD → cable → hub → USB controller → motherboard port
```

Hoặc:

```text
Monitor: 4K 144 Hz capable
Current: 4K 60 Hz

GPU: capable
Monitor: capable
Current adapter/cable path: limiting factor
```

PC Control cần hiểu **hardware path**, không chỉ inventory từng device độc lập.

### 3.9 Backup file không bảo toàn “cách chiếc PC hoạt động”

Người dùng có thể backup Documents hoặc clone ổ đĩa, nhưng thứ họ thực sự mất sau reinstall thường là:

- app set;
- app settings;
- development environment;
- Windows settings;
- device routing;
- WSL/Docker state;
- profiles;
- workflow quen thuộc.

PC Control định nghĩa khái niệm **PC Blueprint**: mô tả có cấu trúc cách chiếc máy được thiết lập và sử dụng, tách khỏi việc clone nguyên hệ điều hành.

---

## 4. Đối tượng người dùng

### 4.1 Người dùng phổ thông

- muốn bật một tính năng nhưng không biết nó nằm ở đâu;
- vừa mua hoặc vừa build PC;
- màn hình, Bluetooth, Wi-Fi, audio hoặc thiết bị USB hoạt động không đúng;
- muốn kiểm tra máy mà không phải cài nhiều utility rời rạc.

### 4.2 Gamer

- cần Secure Boot/TPM cho game;
- muốn bảo đảm màn hình, Game Mode, VRR, HDR và ReBAR được cấu hình đúng;
- muốn chuyển nhanh toàn bộ PC sang Gaming profile;
- cần biết driver/update nào vừa làm game kém ổn định.

### 4.3 Developer

- cần chuẩn bị WSL, Docker, Hyper-V, SSH, Developer Mode, Long Paths và Dev Drive;
- muốn chuyển Docker, WSL, Android SDK hoặc cache khỏi ổ C;
- muốn sao lưu và phục hồi môi trường phát triển.

### 4.4 Streamer và creator

- cần đổi đồng thời audio, microphone, camera, power mode, monitor và app;
- cần lưu/khôi phục OBS scenes, profiles, hotkeys và cấu hình thiết bị.

### 4.5 Người hỗ trợ kỹ thuật

- cần một báo cáo máy đầy đủ nhưng đã loại bỏ dữ liệu nhạy cảm;
- cần lịch sử thay đổi, crash, driver và cấu hình liên quan;
- cần workflow sửa lỗi có từng bước và kết quả kiểm chứng.

---

## 5. Những gì dự án không hướng tới

PC Control không nên trở thành:

- chatbot điều khiển PC;
- AI-first app hoặc hệ thống hiểu câu tự nhiên bắt buộc;
- công cụ debloat cực đoan;
- “one-click FPS booster”;
- antivirus;
- trình dọn rác tổng quát cạnh tranh bằng số GB giả tạo;
- driver updater lấy driver từ nguồn không chính thức;
- bản sao của PowerToys gồm calculator, clipboard, batch rename và các utility nhỏ;
- phần mềm overclock tự động thiếu giới hạn an toàn;
- công cụ flash BIOS đại trà khi chưa có cơ chế vendor-specific đủ tin cậy.

Ứng dụng có thể có **search theo tên, từ khóa và nhóm chức năng thông thường**, nhưng không phụ thuộc vào Intent Engine hay mô hình AI.

---

## 6. Nguyên tắc thiết kế bắt buộc

### 6.1 Minh bạch trước khi thay đổi

Mọi action phải hiển thị tối thiểu:

| Trường | Ý nghĩa |
|---|---|
| What | Setting hoặc thành phần nào sẽ đổi |
| Why | Vì sao người dùng có thể muốn thay đổi |
| Before | Trạng thái hiện tại |
| After | Trạng thái dự kiến |
| Risk | Rủi ro và trường hợp không nên áp dụng |
| Restart | Có cần restart/log out/reconnect hay không |
| Reversible | Có thể tự động hoàn tác, hoàn tác có điều kiện hay không thể hoàn tác |
| Source | Nguồn thực thi: Windows API, PowerShell, vendor API, firmware guide... |

### 6.2 Không thay đổi nếu chưa có xác nhận

Scan và recommendation là read-only. Chỉ khi người dùng bấm `Apply` ứng dụng mới được phép thay đổi hệ thống.

### 6.3 Quyền tối thiểu

Ứng dụng chạy quyền user ở trạng thái bình thường. Chỉ action cần quyền quản trị mới gọi privileged helper và chỉ cấp quyền cho đúng tập thay đổi đã được duyệt.

### 6.4 Có thể kiểm chứng

Không coi exit code bằng 0 là đủ. Mỗi action phải có phép kiểm tra trạng thái sau khi chạy.

### 6.5 Hoàn tác khi có thể

Trước khi apply, phải lưu giá trị cũ và chiến lược rollback. Action không thể hoàn tác phải được đánh dấu rõ và yêu cầu xác nhận mạnh hơn.

### 6.6 Không che giấu sự không chắc chắn

Nếu app không thể đọc hoặc sửa đáng tin cậy một BIOS setting, nó phải hiển thị `Guided` hoặc `Unsupported`, không giả vờ đã hoàn thành.

### 6.7 Nguồn chính thức trước

Driver, firmware và hướng dẫn nhạy cảm phải ưu tiên Microsoft, OEM hoặc nhà sản xuất phần cứng. Không xây mô hình “Driver Booster” từ kho driver không rõ nguồn.

### 6.8 Local-first

Thông tin phần cứng, history, snapshot và cấu hình mặc định được lưu cục bộ. Upload báo cáo, đồng bộ hoặc backup cloud phải là opt-in.

### 6.9 Đọc trước, ghi sau

Với mọi capability, khả năng **quan sát** (đọc trạng thái thật, verify sau thay đổi) là yêu cầu bắt buộc và phải hoạt động trên mọi máy được hỗ trợ. Khả năng **ghi tự động** là tùy chọn, chỉ được bật khi có interface chính thức từ vendor. Một capability chỉ đọc được vẫn là capability đầy đủ về mặt sản phẩm: app phát hiện, lập plan, hướng dẫn và kiểm chứng; người dùng chỉ thực hiện đúng một bước thủ công có hướng dẫn.

### 6.10 Người không rành kỹ thuật phải hiểu được

Mọi finding, action và kết quả phải có một lớp diễn đạt bằng ngôn ngữ đời thường trước, tên kỹ thuật sau. Tên kỹ thuật (SVM, VMP, ReBAR, MT/s) không được xuất hiện một mình ở tầng Simple. Chuẩn tối thiểu cho một finding: *máy bạn đang thế nào → bạn được gì nếu sửa → rủi ro là gì → mất bao lâu, có cần restart không*.

### 6.11 Không làm phiền

App chỉ lên tiếng khi có điều đáng nói. Không có thông báo “máy bạn đã được tối ưu”, không nag về drift người dùng đã chấp nhận, không popup định kỳ. Mặc định là im lặng; mọi thông báo chủ động đều có thể tắt theo từng loại.

---

## 7. Kiến trúc chức năng tổng thể

Sản phẩm được chia thành bốn tầng chức năng:

| Tầng | Vai trò | Module tiêu biểu |
|---|---|---|
| Daily Use | Điều khiển trạng thái dùng hằng ngày | Profiles, Display, Audio, Devices, Temporary Mode |
| Configuration | Cấu hình Windows và phần cứng | BIOS/UEFI, Windows Features, Network, Power |
| Maintenance & Fix | Giữ máy ổn định và sửa sự cố | Updates, Diagnostics, Repair, Recovery, Checkup, Timeline |
| PC Lifecycle | Bảo toàn và chuyển môi trường | Desired State, History, App Settings Vault, PC Blueprint, Migration, Support Report |

Bên dưới các module là **bảy lõi dùng chung**:

1. **PC Capability Graph** — mô hình hóa hardware, firmware, driver, OS feature, setting, app, dependency, action và verification.
2. **PC State Compiler** — nhận current state + desired state/outcome rồi tính difference plan phù hợp với đúng chiếc PC.
3. **Transactional Action Engine** — detect, preflight, stage, order, apply, checkpoint, restart/resume, verify và rollback theo từng action.
4. **Change Basket** — UX để người dùng review transaction trước khi áp dụng.
5. **Desired State & Drift Engine** — lưu baseline/expected state và phát hiện khi máy lệch khỏi trạng thái đó.
6. **PC Change Timeline** — hợp nhất lịch sử update, setting, driver, firmware, crash và execution để hỗ trợ regression analysis.
7. **PC Blueprint** — biểu diễn có cấu trúc “cách chiếc PC hoạt động” để reinstall/migration mà không restore mù quáng.

```text
Adapters / Sensors
       ↓
Capability Graph
       ↓
State Compiler
       ↓
Transaction Plan
       ↓
Apply / Restart / Resume / Verify
       ↓
History + Timeline
       ↓
Desired State / Drift
       ↓
Blueprint / Migration
```

Các lõi này là khác biệt kiến trúc của sản phẩm. UI có thể thay đổi, số lượng setting có thể tăng dần, nhưng logic trạng thái, dependency, transaction và verification phải được giữ ổn định từ đầu.

---

## 8. Hệ thống chức năng

### 8.1 Overview và PC Checkup

Trang Overview tóm tắt máy và các phát hiện đáng chú ý:

```text
PC Setup Score: 82/100

5 findings
⚠ DDR5-6000 is running at 4800 MT/s
⚠ 180 Hz monitor is running at 60 Hz
⚠ System Restore is disabled
⚠ Chipset driver update is available
⚠ SSD firmware update is available
```

Điểm số chỉ là cách trình bày; từng finding phải có bằng chứng cụ thể. Không trừ điểm vì các tweak chủ quan hoặc vì user không dùng dịch vụ trả phí.

### 8.2 Hardware Center

Hiển thị phần cứng theo nhu cầu phổ thông:

- CPU, GPU, RAM và motherboard;
- BIOS/UEFI;
- SSD/HDD;
- monitor;
- network adapter;
- audio, USB và peripheral;
- pin và bộ sạc trên laptop.

Điểm khác biệt không phải số lượng sensor mà là kết nối hardware với action:

```text
Kingston DDR5-6000, 32 GB
Rated speed:   6000 MT/s
Current speed: 4800 MT/s
Finding:       EXPO may be disabled
```

### 8.3 BIOS / UEFI Center

BIOS/UEFI Center được thiết kế trên một thực tế kỹ thuật: **Windows cho phép đọc hệ quả của phần lớn setting firmware trên gần như mọi máy, nhưng không có API chuẩn nào để ghi setting firmware.** Ghi tự động chỉ tồn tại khi vendor cung cấp interface riêng (chủ yếu dòng doanh nghiệp của Dell, Lenovo, HP). Vì vậy module này tách thành hai khả năng độc lập:

| Khả năng | Phạm vi | Cơ chế |
|---|---|---|
| **Observe** (đọc + verify) | Gần như mọi máy Windows 11 | WMI/CIM, PowerShell, registry, SMBIOS, UEFI variables, driver GPU |
| **Write** (đổi tự động) | Chỉ máy có vendor adapter đã xác minh | Vendor WMI/CLI chính thức |

Observe là bắt buộc và là nền cho mọi thứ khác: phát hiện mismatch, lập plan, verify sau restart, drift detection, Timeline. Write là tiện ích cộng thêm cho một số dòng máy.

#### 8.3.1 Capability ưu tiên và nguồn evidence khi đọc

| Capability | Đọc từ Windows | Độ tin cậy | Ghi chú |
|---|---|---|---|
| CPU Virtualization (VT-x/SVM) | `Win32_Processor.VirtualizationFirmwareEnabled`, Hyper-V requirements, Task Manager | Rất cao | Cùng nguồn Task Manager dùng |
| Secure Boot | `Confirm-SecureBootUEFI`, `HKLM\...\SecureBoot\State` | Rất cao | |
| TPM 2.0 | `Get-Tpm`, `Win32_Tpm` | Rất cao | Version, enabled, ready, owned |
| Boot mode UEFI/CSM | firmware environment, `bcdedit`, `$env:firmware_type` | Rất cao | |
| RAM rated vs current (XMP/EXPO) | `Win32_PhysicalMemory.Speed` vs `ConfiguredClockSpeed` | Cao | Rated đọc từ SPD do firmware báo; một số OEM điền thiếu |
| Resizable BAR | NVIDIA/AMD driver API, PCI config space của GPU | Trung bình–cao | Phụ thuộc driver GPU; nếu không đọc được → `Unknown` |
| Above 4G Decoding | suy ra từ ReBAR + MMIO window | Trung bình | Chỉ hiện khi có evidence |
| IOMMU / VT-d | `Get-ComputerInfo`, DMA guard status | Cao | |
| BIOS version, ngày, vendor, model | `Win32_BIOS`, `Win32_BaseBoard`, SMBIOS | Cao | Dùng để chọn Guided path đúng model |
| Fast Boot, Wake on LAN, AC power loss | không đọc trực tiếp; một số suy ra từ NIC/power state | Thấp | Mặc định `Unknown` |
| Fan curve, boot order chi tiết, toggle riêng của hãng | không đọc được | — | `Unknown`, không đoán |

Mọi giá trị đọc được phải kèm `evidence_source` và `confidence`. `Unknown` là trạng thái hợp lệ; app không được suy ra `Disabled` khi đọc thất bại.

#### 8.3.2 Bốn chế độ của một setting

| Chế độ | Ý nghĩa | Ai gặp |
|---|---|---|
| **Auto** | Đọc và đổi tự động qua vendor interface chính thức; vẫn verify sau restart | Dell/Lenovo/HP có adapter đã xác minh |
| **Guided** | Đọc được; app chuẩn bị mọi thứ, hướng dẫn đúng model, restart vào firmware UI, tự resume và verify khi quay lại Windows | Desktop board ASUS/MSI/Gigabyte/ASRock, laptop tiêu dùng, máy chưa có adapter — **đây là đường mặc định** |
| **Read-only** | Đọc được nhưng không có đường đổi an toàn, kể cả guided | Setting bị OEM khóa, có BIOS password không rõ |
| **Unsupported** | Không đọc được và chưa có adapter | Hardware chưa xác minh |

Guided không phải fallback. Với persona gamer và người tự build PC, Guided là trải nghiệm chính, nên phải được đầu tư như một tính năng hoàn chỉnh, không phải màn hình lỗi.

#### 8.3.3 Guided flow — thiết kế đầy đủ

Nguyên tắc: **mọi việc app làm được thì làm trước và sau restart; người dùng chỉ làm đúng một việc ở giữa.**

```text
TRƯỚC RESTART (app làm)
├─ Đọc model board/BIOS version → chọn hướng dẫn đúng vendor + tên gọi đúng
│    Intel: "Intel Virtualization Technology" / "VT-x"
│    AMD:   "SVM Mode"
│    Gigabyte: Tweaker → Advanced CPU Settings → SVM Mode
│    ASUS:     Advanced → CPU Configuration → SVM Mode
│    MSI:      OC → CPU Features → SVM Mode
├─ Hiện trước toàn bộ hướng dẫn: đường dẫn menu, ảnh menu (nếu có), phím vào BIOS, cách lưu (F10)
├─ Gửi hướng dẫn sang điện thoại: QR code → trang tĩnh offline-capable, hoặc In ra
├─ Preflight: BitLocker (mục 10.3), nguồn điện, công việc chưa lưu, BIOS password?
├─ Lưu checkpoint + pending change {target, expected value, verify method}
└─ [Restart vào BIOS] hoặc [Để sau / Nhắc tôi lúc ...]

TRONG BIOS (người dùng làm)
└─ Một thao tác theo hướng dẫn đã có trên điện thoại/giấy

SAU KHI BOOT (app làm)
├─ Tự resume transaction
├─ Đọc lại giá trị thật từ Windows (không hỏi "bạn đã bật chưa?")
├─ Verify đạt → Completed, ghi Timeline before/after + restart ID
└─ Verify không đạt → nhánh 8.3.4, không đánh Failed mù
```

Hướng dẫn theo model được đóng gói dưới dạng **signed guide data** (vendor → family → tên gọi → đường dẫn menu → ảnh), cập nhật độc lập với app, cộng đồng có thể đóng góp theo quy trình review (mục 17, 24 Phase 6). Khi chưa có dữ liệu cho model cụ thể, app dùng hướng dẫn cấp vendor và nói rõ “hướng dẫn chung cho ASUS, menu có thể khác một chút”.

#### 8.3.4 Nhánh “không thành công” của Guided

Sau khi boot lại mà verify không đạt, app hỏi người dùng đã gặp gì và xử lý tương ứng:

| Người dùng chọn | App phản hồi |
|---|---|
| Tôi không tìm thấy mục này | Gợi ý tên gọi khác của hãng, vị trí ở chế độ Advanced/Easy mode, hướng dẫn tìm kiếm trong BIOS (F9 trên một số hãng); cho phép đánh dấu “Not found on this model” để cải thiện guide data |
| Tôi đã bật nhưng app vẫn báo tắt | Kiểm tra lại evidence khác (Hyper-V requirements); kiểm tra xem Hyper-V/VBS có đang chiếm hypervisor không; kiểm tra BIOS đã lưu chưa (F10 vs Esc) |
| Mục này bị mờ / không đổi được | Kiểm tra BIOS password, chế độ OEM lock; chuyển setting sang Read-only với lý do |
| Tôi chưa vào BIOS | Giữ pending, hỏi có muốn thử lại hay hoãn |
| Bỏ qua bước này | Transaction → Partially Completed; outcome hiện “Not reached”; các action khác vẫn được ghi kết quả riêng |

Không có nhánh nào kết thúc bằng thông báo `Failed` mà không giải thích và không có bước tiếp theo.

#### 8.3.5 Auto mode — điều kiện để bật

Một vendor adapter chỉ được cấp phép Write khi:

- dùng interface chính thức có tài liệu (Dell WMI/Command Configure, Lenovo WMI, HP BCU hoặc tương đương);
- đã test trên tối thiểu 3 model của dòng đó với cả trạng thái có/không BIOS password;
- có đường xử lý khi setting yêu cầu xác nhận vật lý lúc boot (physical presence prompt);
- Secure Boot, TPM và setting ảnh hưởng boot vẫn qua BitLocker preflight;
- verify sau restart chạy y như Guided — Auto không bỏ qua verify.

Tuyệt đối không dùng kỹ thuật ghi trực tiếp vào biến `Setup` của UEFI theo offset (không tài liệu, khác nhau từng BIOS version, có thể làm máy không boot). Điều này trái với nguyên tắc 6.7 và 19.2 mức Critical.

### 8.4 Windows Feature Center

Gom các tính năng đang nằm rải rác:

- Hyper-V;
- WSL và WSL 2;
- Virtual Machine Platform;
- Windows Hypervisor Platform;
- Windows Sandbox;
- OpenSSH Client/Server;
- Remote Desktop;
- IIS;
- SMB;
- .NET Framework optional features;
- Developer Mode;
- Long Paths;
- System Restore;
- BitLocker và Core Isolation;
- Game Mode và HAGS.

Khi tắt một capability, app phải phân tích dependency. Ví dụ tắt Hyper-V cần cảnh báo Docker Desktop, WSL 2 và Windows Sandbox có thể bị ảnh hưởng.

### 8.5 Driver & Firmware Center

- inventory driver/firmware hiện tại;
- kiểm tra update từ nguồn chính thức;
- release notes và mức độ quan trọng;
- snapshot trạng thái liên quan trước update;
- hỗ trợ rollback nếu nền tảng/vendor thực sự cho phép;
- theo dõi lỗi phát sinh sau update.

Không tự động cập nhật toàn bộ driver. BIOS update luôn cần policy riêng theo vendor, nguồn điện, BitLocker recovery key và khả năng phục hồi.

### 8.6 Display Center

- resolution và scaling;
- refresh rate;
- HDR và VRR;
- orientation;
- primary monitor;
- multi-monitor layout;
- color depth và GPU scaling nếu adapter hỗ trợ;
- sleep/display timeout;
- DDC/CI brightness nếu màn hình hỗ trợ.

Recommendation tiêu biểu:

> Màn hình hỗ trợ 180 Hz nhưng hiện đang chạy 60 Hz.

### 8.7 Network Center

- DHCP/static IP;
- DNS;
- gateway, connectivity và DNS diagnostics;
- proxy;
- MTU;
- adapter state và power saving;
- network discovery;
- SMB/RDP-related settings;
- firewall rule visibility;
- port status;
- Wake on LAN;
- Wi-Fi information và signal quality.

Mọi thay đổi có thể làm mất kết nối phải có countdown/revert hoặc đường phục hồi cục bộ.

### 8.8 USB & Peripheral Center

- cây controller, hub, port và thiết bị;
- tốc độ capability so với tốc độ link thực tế;
- reset, disable/enable và safe eject;
- power management;
- driver status;
- ghi nhớ cấu hình thiết bị nếu có adapter vendor.

Port Advisor có thể phát hiện:

- SSD 10 Gbps đang chạy 5 Gbps;
- màn hình 4K 144 Hz bị giới hạn bởi cổng/cáp hiện tại;
- charger USB-C không đủ công suất;
- thiết bị đang cắm qua hub gây giới hạn bandwidth.

### 8.9 Audio, Microphone & Camera Hub

- default input/output;
- format, sample rate và bit depth;
- per-app audio routing khi Windows API hỗ trợ;
- spatial audio;
- microphone level và enhancement;
- camera resolution, frame rate và basic controls;
- lưu state vào PC Profile.

### 8.10 Startup & Background Center

Gom:

- startup apps;
- services;
- scheduled tasks;
- background apps;
- shell extensions;
- context menu entries.

Mỗi mục phải giải thích chức năng, publisher, startup impact, dependency đã biết và recommendation. Không tự động disable service chỉ vì service đang chạy.

### 8.11 Gaming Center

- Secure Boot và TPM readiness theo game;
- Game Mode;
- HAGS;
- VRR/HDR;
- refresh rate;
- Resizable BAR;
- GPU power profile nếu có adapter;
- controller status;
- Xbox-related services;
- fullscreen optimization ở phạm vi được hỗ trợ.

Ứng dụng có thể kiểm tra preset theo game, ví dụ Valorant, nhưng từng yêu cầu phải là rule có version và nguồn tham chiếu, không phải suy đoán AI.

### 8.12 Developer Center

- virtualization readiness;
- WSL/WSL 2;
- Hyper-V và Windows Sandbox;
- Docker prerequisites;
- SSH;
- Developer Mode;
- Long Paths;
- Git, PowerShell và Windows Terminal status;
- Dev Drive;
- Android emulator prerequisites;
- environment variables overview;
- Docker/WSL/SDK storage location.

### 8.13 Power & Thermal Center

- Windows power mode và power plans;
- sleep, hibernate và wake timers;
- CPU min/max state;
- USB selective suspend;
- PCIe power saving;
- GPU power mode nếu vendor hỗ trợ;
- battery health và charge limit trên laptop;
- thermal sensor summary;
- liên kết hoặc adapter đến fan control chính hãng.

Không tự đặt fan curve hoặc undervolt nếu chưa có kiểm tra tương thích, giới hạn và rollback đáng tin cậy.

### 8.14 Diagnostics Center

Mỗi sự cố được biểu diễn bằng checklist dependency có thể kiểm tra:

```text
Bluetooth
✓ Hardware detected
✓ Driver loaded
✗ Bluetooth service stopped
✓ Airplane mode off
⚠ Adapter power saving enabled
```

Các workflow ban đầu:

- Bluetooth không hoạt động hoặc thường xuyên disconnect;
- Wi-Fi có adapter nhưng không vào Internet;
- audio device có nhưng không phát tiếng;
- microphone không được app sử dụng;
- màn hình không chạy đúng refresh rate;
- USB device bị lỗi/không ổn định;
- Windows Update bị kẹt;
- WSL/Docker prerequisites chưa đúng.

### 8.15 Repair Center

Repair workflow phải cho xem từng bước trước khi chạy, ví dụ:

```text
Windows Update Repair
1. Inspect services and current state
2. Stop required services
3. Reset update cache if corruption is detected
4. Restart services
5. Re-run health check
6. Verify Windows Update status
```

Không chạy hàng loạt lệnh “repair everything” khi chưa xác định subsystem lỗi.

### 8.16 Recovery Center

- restore points;
- Safe Mode;
- Startup Repair;
- Windows Recovery Environment;
- System File Checker/DISM workflow;
- driver rollback;
- boot options;
- recovery USB guidance;
- BIOS recovery information theo model;
- last known good configuration do PC Control ghi nhận.

### 8.17 Storage Center & Smart Storage Mover

Phân tích theo workload thay vì chỉ theo folder:

```text
Steam games       312 GB
Docker            106 GB
WSL                82 GB
Adobe cache        41 GB
Downloads          28 GB
Android SDK        19 GB
Package caches       9 GB
```

Các workflow di chuyển ưu tiên:

- Steam game/library;
- WSL distro;
- Docker data;
- Android SDK;
- ứng dụng/cache được hỗ trợ rõ ràng.

Mỗi mover phải có preflight, kiểm tra dung lượng, backup metadata, verify và rollback. Không di chuyển folder hệ thống bằng junction chung chung nếu chưa hiểu ứng dụng.

### 8.18 PC Profiles / Scenes

Profile thay đổi đồng thời nhiều lớp của PC:

```text
Gaming
- Power mode: Performance
- Monitor: 180 Hz
- HDR: On
- Default audio: Headset
- Game Mode: On
- Start: Steam, Discord
- Close: selected work apps
- Sleep: Never while profile is active
```

Các profile mặc định có thể gồm:

- Gaming;
- Work;
- Streaming;
- Meeting;
- Night;
- Battery Saving.

Profile phải có chế độ **activate/deactivate**, lưu state trước khi kích hoạt và tránh ghi đè setting mà người dùng đã chủ động sửa trong lúc profile đang chạy.

### 8.19 Temporary Mode

Các thay đổi có thời hạn và tự hoàn tác:

- giữ máy thức trong 2 giờ;
- tắt sleep cho đến khi download hoàn thành;
- Performance Mode trong 1 giờ;
- tắt webcam cho đến lần reboot tiếp theo;
- tắt Bluetooth đến một thời điểm đã chọn.

### 8.20 App Settings Vault

Sao lưu có version cho cấu hình ứng dụng:

- VS Code settings, snippets và keybindings;
- OBS scenes, profiles và hotkeys;
- game graphics, sensitivity và keybind;
- cấu hình trong AppData, ProgramData, Documents hoặc Registry;
- các ứng dụng khác thông qua adapter/recipe đã xác minh.

Vault không mặc định sao lưu token, cookie, password, license key hoặc dữ liệu nhạy cảm. Mỗi adapter cần manifest khai báo rõ file/registry nào được đưa vào backup.

### 8.21 New PC / Reinstall Migration

Mục tiêu là **tái tạo môi trường**, không clone nguyên hệ điều hành.

Package migration có thể chứa:

- danh sách ứng dụng và nguồn cài;
- app configs từ Vault;
- Windows settings được hỗ trợ;
- fonts;
- environment variables;
- Git/SSH metadata theo lựa chọn rõ ràng;
- WSL distro/export reference;
- device settings và PC Profiles.

Trên máy mới, app tạo difference plan:

```text
✓ 27 applications already installed
✗ 36 applications missing
✗ VS Code settings missing
⚠ GPU and motherboard are different
```

Các setting phụ thuộc phần cứng không được phục hồi mù quáng.

### 8.22 Support Report

Tạo package hỗ trợ gồm:

- model và hardware summary;
- BIOS và driver versions;
- Windows build;
- recent updates;
- relevant settings;
- crash history và event log đã lọc;
- nhiệt độ/sensor tại thời điểm chụp nếu có;
- lịch sử thay đổi liên quan.

Trước khi export, app hiển thị privacy review và mặc định loại:

- username;
- IP công khai/nội bộ nếu không cần;
- Wi-Fi password;
- serial number;
- license key/token;
- đường dẫn và tên file cá nhân.

### 8.23 App Dependency Checker

Trước khi uninstall runtime hoặc disable capability, app liệt kê phần có thể bị ảnh hưởng:

```text
Disable Hyper-V
Affected capabilities:
- Docker Desktop
- WSL 2
- Windows Sandbox
```

Ở giai đoạn đầu, dependency chỉ được kết luận khi có bằng chứng chắc chắn. Các liên hệ suy đoán phải ghi rõ mức confidence.

---

## 9. Change Basket & Transaction Engine — trải nghiệm trung tâm

Người dùng có thể chỉnh nhiều mục nhưng chưa apply ngay:

```text
Pending Changes (7)

BIOS
SVM                 Off → On
Resizable BAR       Off → On

Windows
Virtual Machine     Off → On
WSL                 Off → On

Display
Refresh rate        60 → 180 Hz

Network
DNS                 Automatic → 1.1.1.1

Power
Mode                Balanced → Performance
```

Action Engine sau đó xây execution plan:

```text
Phase 1 — Create snapshot and run preflight checks
Phase 2 — Apply online Windows/display/network changes
Phase 3 — Verify online changes
Phase 4 — Restart required
Phase 5 — Apply or guide BIOS changes
Phase 6 — Resume after boot
Phase 7 — Verify final state
```

### 9.1 Yêu cầu của Change Basket

- phát hiện action xung đột;
- tự sắp xếp theo dependency;
- chia execution theo restart boundary;
- tính trước action nào cần elevation;
- hiển thị action không thể rollback;
- khóa plan bằng hash trước khi chuyển cho privileged helper;
- lưu checkpoint để tiếp tục sau reboot;
- báo kết quả riêng cho từng action, không chỉ báo “Done” chung.

### 9.2 Trạng thái của một change set

```text
Draft
→ Ready
→ Applying
→ Awaiting Restart
→ Resuming
→ Verifying
→ Completed | Partially Completed | Failed | Rolled Back
```


### 9.3 Transaction semantics

Change Basket không được triển khai như một danh sách checkbox rồi chạy tuần tự. Một change set phải được coi như một **transaction có nhiều phase**.

Transaction không đảm bảo ACID như database, vì một số thay đổi hệ thống hoặc firmware không thể rollback tuyệt đối. Tuy nhiên hệ thống phải cung cấp semantics rõ ràng:

- biết action nào đã được commit;
- biết action nào mới stage;
- biết action nào đang chờ reboot;
- biết action nào verify thất bại;
- biết phần nào có thể rollback độc lập;
- không báo toàn transaction thành công nếu một outcome bắt buộc chưa đạt;
- giữ đủ checkpoint để tiếp tục hoặc phục hồi sau crash/restart.

Ví dụ:

```text
Transaction #184

PRECHECK
├─ Compatibility
├─ Permissions
├─ Recovery availability
└─ Snapshot

PHASE A — Online / reversible
├─ Display 60 → 180 Hz
├─ DNS Auto → 1.1.1.1
└─ Power Balanced → Performance

VERIFY A
├─ Display ✓
├─ DNS ✓
└─ Power ✓

PHASE B — Windows feature changes
├─ Virtual Machine Platform
└─ WSL

RESTART BOUNDARY

PHASE C — Firmware
└─ SVM Mode: Guided

RESTART / RETURN TO WINDOWS

VERIFY FINAL
├─ SVM ✓
├─ VMP ✓
├─ WSL 2 ✓
└─ Docker readiness ✓
```

### 9.4 Partial completion policy

Nếu một transaction chỉ hoàn thành một phần, app phải hiển thị chính xác:

```text
Completed: 4
Failed: 1
Pending: 2

Outcome: Not reached
Safe rollback available for: 3 actions
Manual recovery required for: 0 actions
```

Không dùng thông báo `Done` chung cho transaction phức tạp.

### 9.5 Transaction provenance

Mỗi transaction phải ghi:

- user intent hoặc outcome đã chọn;
- capability graph version;
- action/recipe version;
- adapter version;
- OS build và hardware compatibility fingerprint;
- plan hash mà user đã review;
- before/requested/actual values;
- verification evidence;
- rollback evidence;
- reboot/session correlation ID.

Dữ liệu này là nền cho History, Timeline, regression analysis và support report.

---

## 10. Safe Apply và Update Guard

### 10.1 Safe Apply

Với thay đổi có nguy cơ mất hình, mất mạng hoặc boot không ổn định:

- chụp state cũ;
- đặt recovery marker;
- apply tạm thời nếu nền tảng hỗ trợ;
- yêu cầu user xác nhận máy vẫn hoạt động;
- tự revert khi timeout hoặc verification thất bại.

Không được hứa auto-recovery cho BIOS nếu firmware/vendor không hỗ trợ một cơ chế rollback thực tế.

### 10.2 Update Guard

Trước khi cập nhật:

- ghi phiên bản hiện tại;
- lưu setting liên quan;
- tạo restore point/snapshot phù hợp nếu có thể;
- kiểm tra nguồn điện, BitLocker và điều kiện riêng;
- đánh dấu mốc thời gian update.

Sau update:

- scan difference;
- kiểm tra device/driver health;
- theo dõi crash hoặc regression;
- đề xuất rollback khi có bằng chứng hợp lý và nền tảng hỗ trợ.

### 10.3 BitLocker / Device Encryption preflight (bắt buộc)

Đây là nguồn “máy hỏng” thực tế lớn nhất khi người dùng phổ thông đụng vào BIOS: đổi Secure Boot, TPM, boot mode, CSM, hoặc thậm chí cập nhật BIOS trên máy có BitLocker/Device Encryption sẽ khiến Windows đòi **recovery key 48 số** lúc boot. Phần lớn laptop Windows 11 bán sẵn có Device Encryption bật mà chủ máy không biết.

Trước mọi action thuộc nhóm firmware/boot, app phải:

1. Đọc trạng thái BitLocker/Device Encryption của ổ hệ thống (`Get-BitLockerVolume`, `manage-bde -status`).
2. Nếu đang bật: hiển thị cảnh báo bằng ngôn ngữ đời thường, giải thích vì sao và hậu quả nếu không có key.
3. Hướng dẫn xác nhận recovery key đang ở đâu (tài khoản Microsoft, file đã lưu, Azure AD) và cách kiểm tra trước khi restart.
4. Đề xuất **suspend BitLocker cho 1 lần restart** (`Suspend-BitLocker -RebootCount 1`) — action có elevation, reversible, tự resume sau boot.
5. Không cho Apply nếu người dùng chưa chọn một trong hai: đã có key / đã suspend.

Với Auto mode trên Dell/Lenovo/HP, bước này vẫn bắt buộc. Kết quả preflight được ghi vào Timeline như một event riêng.

---

## 11. PC Capability Graph

Capability Graph mô hình hóa mối quan hệ giữa:

- thiết bị;
- driver/firmware;
- OS feature;
- setting;
- ứng dụng;
- dependency;
- action;
- verification rule.

Ví dụ Docker readiness:

```text
Docker Desktop
├── CPU supports virtualization
├── BIOS virtualization enabled
├── Virtual Machine Platform enabled
├── WSL enabled
├── WSL default version = 2
└── supported Windows version
```

Graph là nền tảng cho:

- recommendation có bằng chứng;
- dependency checker;
- diagnostics;
- execution ordering;
- migration compatibility;
- support report.


### 11.1 Graph node types

Graph tối thiểu cần hỗ trợ:

```text
HardwareDevice
FirmwareCapability
Driver
OSFeature
Setting
Application
Workload
ConnectionPath
Dependency
Constraint
Action
VerificationRule
DesiredStateRule
Evidence
```

### 11.2 Edge semantics

Không chỉ dùng edge `depends_on`. Cần phân biệt:

- `requires`;
- `supports`;
- `configured_by`;
- `provided_by`;
- `conflicts_with`;
- `limits`;
- `connected_through`;
- `verified_by`;
- `changed_by`;
- `affects`;
- `compatible_with`.

Ví dụ:

```text
Docker Desktop
  requires → WSL 2
WSL 2
  requires → Virtual Machine Platform
Virtual Machine Platform
  requires → Firmware Virtualization
Firmware Virtualization
  supported_by → CPU
```

### 11.3 Evidence-first graph

Một relation chỉ được dùng để tự động thay đổi máy khi có evidence đủ mạnh. Graph nên lưu:

```text
source
confidence
scope
os_build
vendor
model
first_seen
last_verified
```

Quan hệ suy đoán có thể phục vụ explanation nhưng không được tự động trở thành privileged action.

---


## 12. PC State Compiler

PC State Compiler là lớp biến một **outcome hoặc desired state** thành execution plan dựa trên trạng thái thực tế của chiếc máy.

Khác với preset hoặc script cố định:

```text
Preset:
Run A + B + C on every machine
```

State Compiler hoạt động như sau:

```text
Current State
    +
Desired State / Outcome
    +
Capability Graph
    +
Compatibility Rules
    ↓
Difference
    ↓
Candidate Actions
    ↓
Dependency Ordering
    ↓
Risk / Restart / Rollback Analysis
    ↓
Execution Plan
```

### 12.1 Outcome thay vì toggle

Ví dụ user chọn:

> **Prepare this PC for Docker Desktop using WSL 2**

Máy A:

```text
CPU virtualization support  ✓
BIOS virtualization         ✗
WSL                          ✓
VMP                          ✗
WSL default version          1
```

Compiler tạo:

```text
1. Enable firmware virtualization
2. Enable Virtual Machine Platform
3. Set WSL default version to 2
4. Restart as required
5. Verify Docker readiness
```

Máy B đã có BIOS virtualization và VMP:

```text
1. Set WSL default version to 2
2. Verify Docker readiness
```

Cùng một outcome nhưng plan khác nhau.

### 12.2 Desired state contract

Một desired state có thể khai báo:

```yaml
id: outcome.docker-wsl2-ready
version: 1.0.0

requires:
  - capability: cpu.virtualization
    state: supported
  - capability: firmware.virtualization
    state: enabled
  - capability: windows.feature.virtual-machine-platform
    state: enabled
  - capability: windows.wsl
    state: enabled
  - capability: wsl.default-version
    state: 2

verify:
  - check: workload.docker-wsl2-readiness
    expected: ready
```

Compiler chịu trách nhiệm tìm action thích hợp, không nhét action trực tiếp vào outcome nếu có thể tránh.

### 12.3 Compiler rules

- không tạo action nếu current state đã đạt;
- không dùng action không compatible với hardware/OS hiện tại;
- ưu tiên route ít rủi ro và reversible hơn;
- không vượt qua `Guided`/`Unsupported` giả tạo;
- phát hiện conflict giữa nhiều desired states;
- tính restart boundary;
- chỉ tạo plan từ action version đã được trust;
- plan phải deterministic với cùng state snapshot + rule versions;
- plan thay đổi sau refresh phải yêu cầu review lại.

### 12.4 Use cases dài hạn

State Compiler có thể phục vụ:

- Docker Ready;
- WSL Development Ready;
- Valorant Requirements Ready;
- Streaming Setup Ready;
- Meeting Setup Ready;
- Battery Saving State;
- Secure Remote Access Ready;
- New PC Blueprint reconciliation.

---

## 13. Desired State & Drift Detection

PC Control không chỉ biết **trạng thái hiện tại**, mà còn có thể biết **trạng thái người dùng mong chiếc PC duy trì**.

### 13.1 Desired State

Desired state có ba nguồn:

1. user pin trực tiếp một setting;
2. một PC Profile/Scene;
3. một outcome hoặc PC Blueprint.

Ví dụ:

```text
My Gaming PC — Expected State

Display
  Refresh rate: 180 Hz

Firmware
  Resizable BAR: Enabled

Windows
  Game Mode: Enabled

Audio
  Default output: HyperX Headset

Power
  Default plan: Balanced
```

### 13.2 Drift

Drift là khi current state không còn khớp expected state.

```text
Expected                    Current
180 Hz                      60 Hz       DRIFT
Resizable BAR: Enabled      Disabled    DRIFT
HyperX Headset              Realtek     DRIFT
Game Mode: Enabled          Enabled     OK
```

### 13.3 Drift classification

Không phải mọi khác biệt đều là lỗi.

| Loại | Ý nghĩa |
|---|---|
| Expected drift | Device tạm thời không có mặt hoặc profile khác đang active |
| User-initiated drift | User chủ động đổi setting ngoài PC Control |
| External drift | Windows/OEM/driver/app khác đã thay đổi |
| Update drift | Thay đổi xuất hiện sau OS/driver/firmware update |
| Unknown drift | Không xác định được nguyên nhân |

PC Control phải tránh tự động “sửa lại” nếu user vừa chủ động thay đổi một setting.

### 13.4 Drift UX

```text
Your PC has drifted from its known-good state

3 meaningful changes since Aug 21

Display
180 Hz → 60 Hz

Firmware
Resizable BAR
Enabled → Disabled

Audio
HyperX Headset → Realtek

[Review Changes] [Restore Selected]
```

Để Drift không trở thành app “càu nhàu”, UX phải tuân theo:

- **Im lặng mặc định**: drift chỉ hiện trong Overview dưới dạng một dòng tóm tắt; không popup, không toast ngay khi phát hiện.
- **Digest**: nhiều drift trong cùng một update/restart window được gom thành một mục với nguyên nhân khả dĩ (“sau Windows Update ngày 21/8”).
- **Accept as new normal**: mỗi item có nút chấp nhận trạng thái mới; Desired State được cập nhật và không nhắc lại. Đây là hành động một chạm, không cần vào Settings.
- **Snooze theo item**: 1 ngày / 1 tuần / cho đến khi có thay đổi mới.
- **Phân biệt nguồn**: drift do chính người dùng đổi (có transaction hoặc thao tác thấy được) không được xem là drift cần cảnh báo; chỉ external drift mới lên tiếng.
- **Không có badge số đỏ vĩnh viễn**: nếu người dùng đã xem, badge biến mất dù chưa xử lý.

### 13.5 Auto-remediation policy

Mặc định drift detection là read-only.

Auto-remediation chỉ được cân nhắc cho action:

- low-risk;
- reversible;
- user đã opt-in;
- không gây gián đoạn;
- có verification;
- không ghi đè thay đổi user vừa chủ động thực hiện.

BIOS, driver, network-critical hoặc boot settings không được tự phục hồi âm thầm.

---

## 14. PC Change Timeline & Regression Intelligence

PC Change Timeline là lịch sử hợp nhất những thay đổi có thể ảnh hưởng đến cách chiếc PC hoạt động.

### 14.1 Event sources

Timeline có thể ingest:

- PC Control transactions;
- Windows Update;
- driver install/update/rollback;
- firmware/BIOS version change;
- app install/uninstall/update khi đọc được đáng tin cậy;
- device attach/detach đáng chú ý;
- default audio/display/network changes;
- profile activation;
- crash/reliability events;
- selected Event Log signals;
- restore/rollback events.

### 14.2 Normalized event

```text
ChangeEvent
- id
- timestamp
- source
- category
- component
- before
- after
- initiator
- evidence
- confidence
- related_transaction
- related_restart
```

### 14.3 “What changed before this problem?”

Ví dụ:

```text
SEP 01

10:31  NVIDIA Driver
       590.22 → 591.04

10:34  Display
       180 Hz → 60 Hz

10:34  G-SYNC
       On → Off

SEP 02

19:21  Valorant crash
19:24  Valorant crash
```

UI có thể trình bày:

```text
Potentially related changes

HIGH
Display refresh rate     180 → 60

HIGH
NVIDIA Driver            590.22 → 591.04

MEDIUM
G-SYNC                   On → Off
```

### 14.4 Regression Intelligence

Regression engine **không được kết luận nhân quả nếu không có bằng chứng**.

Nó chỉ được nói các mức như:

- temporal correlation;
- configuration mismatch;
- known dependency relation;
- repeated pattern;
- verified causal rollback.

Ví dụ wording hợp lệ:

> Hai thay đổi này xảy ra ngay trước khi crash bắt đầu.

Không hợp lệ nếu chưa có evidence:

> Driver NVIDIA chắc chắn gây crash.

### 14.5 Known-good checkpoints

User có thể đánh dấu:

```text
Known Good — Aug 21
```

Checkpoint gồm:

- selected important settings;
- driver/firmware versions;
- active profiles;
- relevant device state;
- capability graph snapshot hash.

Sau này PC Control có thể so:

```text
Current vs Known Good
```

và tạo restore plan cho phần reversible.

---

## 15. Hardware Path Intelligence

Hardware Center không chỉ liệt kê thiết bị. PC Control cần có khả năng mô hình hóa **đường kết nối và bottleneck** giữa các thành phần.

### 15.1 Connection Path

Ví dụ USB:

```text
Samsung T9 SSD
  ↓
USB-C cable
  ↓
USB Hub
  ↓
USB Controller
  ↓
Motherboard Port
```

Mỗi edge có thể mang:

```text
theoretical_capability
negotiated_link
protocol
power_delivery
lane/bandwidth constraint
evidence
```

### 15.2 Bottleneck explanation

```text
Samsung T9
Rated capability: 20 Gbps
Current negotiated link: 5 Gbps

Likely limiting segment:
USB Hub Port 3 — 5 Gbps

Alternative detected:
Rear USB-C Port 1 — up to 20 Gbps
```

### 15.3 Display path

```text
GPU
 ↓
Output port
 ↓
Cable / adapter / dock
 ↓
Monitor input
 ↓
Panel mode
```

PC Control có thể trả lời:

- vì sao 4K 144 Hz chỉ chạy 60 Hz;
- vì sao HDR option không xuất hiện;
- vì sao VRR không hoạt động;
- vì sao color depth bị giới hạn;
- path nào có khả năng đáp ứng mode mong muốn.

### 15.4 Power path

Trên laptop/USB-C:

```text
Charger rated: 100 W
Negotiated: 65 W

Path:
Charger → cable → dock → laptop
```

App chỉ đưa recommendation khi có telemetry/evidence đủ tin cậy.

### 15.5 Hardware Path policy

- không suy đoán capability chỉ từ marketing name;
- phân biệt theoretical maximum và negotiated/current;
- hiển thị đoạn path chưa biết dưới dạng `Unknown`;
- không khuyến nghị mua phần cứng cụ thể nếu chưa xác định bottleneck;
- dùng vendor/OS telemetry chính thức khi có thể.

---

## 16. PC Blueprint — bảo toàn cách chiếc PC hoạt động

PC Blueprint là representation có version của **môi trường sử dụng**, không phải disk image.

### 16.1 Blueprint content

```text
PC Blueprint

Applications
Development Environment
Windows Settings
Device Preferences
Display Layout
Audio Routing
Profiles
App Settings Vault
Environment Variables
Fonts
WSL Metadata
Storage Placement
Supported Firmware Preferences
```

Không mặc định chứa:

- password;
- session cookie;
- token;
- private key;
- license secret;
- dữ liệu cá nhân không cần thiết.

### 16.2 Blueprint reconciliation

Khi áp dụng lên máy mới:

```text
Blueprint
    +
New Machine Capability Graph
    ↓
Compatibility Analysis
    ↓
Difference Plan
```

Ví dụ:

```text
✓ 31 Windows settings transferable
✓ 42 applications available
✓ VS Code configuration compatible
✓ WSL distro can be restored

⚠ AMD GPU → NVIDIA GPU
  GPU-specific settings skipped

⚠ 180 Hz monitor → 240 Hz monitor
  Exact old refresh value will not be restored blindly

⚠ Different motherboard
  Firmware settings require new compatibility evaluation
```

### 16.3 Blueprint levels

| Level | Nội dung |
|---|---|
| Light | apps + selected Windows settings + profiles |
| Standard | Light + app configs + dev environment + device preferences |
| Advanced | Standard + WSL/storage mappings + supported firmware preferences |

### 16.4 Versioning

Blueprint phải có version và diff:

```text
Blueprint v12
+ OBS profile
+ New monitor layout
~ Power profile
- Old Android SDK path
```

User có thể chọn restore toàn bộ hoặc chọn từng domain.

### 16.5 Blueprint vs backup

Backup trả lời:

> File của tôi ở đâu?

Blueprint trả lời:

> Chiếc PC của tôi được thiết lập để hoạt động như thế nào?

Hai khái niệm có thể bổ trợ nhau nhưng không nên nhập làm một.

---

## 17. Mô hình Action/Recipe

Mỗi chức năng tự động hóa được khai báo bằng một manifest có version thay vì viết logic rải rác trong UI.

Ví dụ minh họa:

```yaml
id: windows.enable-wsl2
version: 1.0.0
title: Enable WSL 2
category: developer
platform:
  os: windows
  minimum_build: "..."

detect:
  - check: cpu.virtualization_supported
  - check: firmware.virtualization_enabled
  - check: windows.feature.WSL
  - check: windows.feature.VirtualMachinePlatform

dependencies:
  - capability: firmware.virtualization
  - capability: windows.virtual_machine_platform

actions:
  - use: windows.enable_feature
    target: VirtualMachinePlatform
  - use: windows.enable_feature
    target: Microsoft-Windows-Subsystem-Linux

restart:
  required: true

verify:
  - check: windows.feature.WSL
    expected: enabled
  - check: wsl.default_version
    expected: 2

rollback:
  supported: true
  strategy: restore_previous_feature_states
```

### 17.1 Recipe policy

- manifest phải được ký và version hóa;
- action nhạy cảm cần code adapter đã review, không chỉ là script tùy ý;
- khai báo permissions và dữ liệu truy cập;
- có OS build/vendor/model compatibility;
- có detection, preflight, verify và rollback;
- community recipe mặc định không được chạy arbitrary elevated code;
- recipe update không được tự thay đổi một pending plan đã được user duyệt.

Plugin/Recipe Store là hướng dài hạn, không phải yêu cầu của MVP.

---

## 18. Kiến trúc kỹ thuật đề xuất

### 18.1 Các thành phần chính

| Thành phần | Trách nhiệm |
|---|---|
| Desktop UI | Navigation, hardware views, settings, basket, history |
| Local Orchestrator | Capability Graph, planning, dependency và workflow state |
| Privileged Helper | Chỉ thực thi action đã ký/allowlist cần quyền admin |
| Adapter Layer | Windows, WMI/CIM, PowerShell, vendor APIs, hardware providers |
| Reboot Coordinator | Lưu checkpoint và tiếp tục plan sau khi boot |
| Local Store | Inventory, snapshots, histories, recipes và profiles |
| Update Service | Cập nhật app, adapter và signed recipe metadata |

### 18.2 Công nghệ phù hợp

Hai hướng khả thi:

**Hướng native Windows ưu tiên độ sâu tích hợp**

- UI: WinUI 3 hoặc WPF;
- runtime: C#/.NET;
- Windows integration: Windows APIs, WMI/CIM, PowerShell SDK;
- local database: SQLite;
- service/helper: Windows Service hoặc elevated helper tối giản.

**Hướng tận dụng TypeScript cho UI**

- UI: React + TypeScript;
- desktop shell: Tauri;
- native core/helper: Rust hoặc C#;
- local database: SQLite.

Không nên dùng Electron-only process để trực tiếp xử lý toàn bộ privileged system logic. UI và privileged execution cần được tách biệt về quyền và trust boundary.

### 18.3 Adapter layer

Adapter tiêu biểu:

- Windows Optional Features;
- Registry có schema/allowlist;
- Services và Scheduled Tasks;
- power configuration;
- display configuration;
- networking;
- WMI/CIM hardware inventory;
- NVIDIA/AMD/Intel khi có API chính thức;
- OEM laptop/mainboard adapters;
- monitor DDC/CI;
- application configuration adapters.

Mỗi adapter phải trả về capability, current state, supported operations, required privilege, restart type, risk class và verification method.

Adapter được chia hai lớp rõ ràng:

- **Observation adapters** — luôn có, không phụ thuộc vendor: WMI/CIM, PowerShell, registry, SMBIOS, UEFI variables, driver GPU. Đây là lớp cho phép Observe/verify BIOS trên mọi máy (mục 8.3.1).
- **Vendor write adapters** — theo dòng máy, chỉ dùng API chính thức, phải qua tiêu chí 8.3.5 mới được cấp phép Write.

### 18.4 Chiến lược hỗ trợ dòng máy (Hardware Support Tiers)

Mục tiêu dài hạn là hỗ trợ càng nhiều dòng máy càng tốt, nhưng không bằng cách hứa Auto cho tất cả. Mỗi dòng máy nằm ở một tier công khai, người dùng thấy tier của máy mình ngay sau lần scan đầu.

| Tier | Ý nghĩa | Ví dụ mục tiêu |
|---|---|---|
| **Tier 1 — Full** | Observe đầy đủ + Auto write cho các firmware setting chính + guide data theo model | Dell (Latitude, OptiPlex, Precision, XPS), sau đó Lenovo (ThinkPad, ThinkCentre), HP (EliteBook, ProBook, ProDesk) |
| **Tier 2 — Guided Verified** | Observe đầy đủ + Guided với guide data đã kiểm chứng theo model/BIOS family + verify sau boot | ASUS, MSI, Gigabyte, ASRock desktop boards; laptop gaming ASUS/MSI/Acer/Lenovo Legion |
| **Tier 3 — Guided Generic** | Observe đầy đủ + Guided theo vendor (chưa có ảnh/đường dẫn theo model) | Mọi máy Windows 11 khác đọc được SMBIOS |
| **Tier 0 — Read-only** | Chỉ inventory và Checkup; không có action ghi firmware | Hardware không nhận diện, máy ảo, máy có OEM lock |

Thứ tự triển khai:

```text
Phase 1   Tier 3 cho mọi máy (Observe + Guided generic)  ← nền bắt buộc
          Tier 2 cho 1–2 board ASUS/MSI trong lab
          Tier 1 cho Dell (adapter đầu tiên, WMI có tài liệu công khai)
Phase 2   Tier 1: Lenovo, HP (cùng mô hình WMI, chi phí adapter thấp hơn)
          Tier 2: mở rộng guide data ASUS/MSI/Gigabyte/ASRock qua cộng đồng
Phase 3+  Tier 2: laptop tiêu dùng; Tier 1 chỉ thêm khi vendor có API chính thức
```

Tiêu chí nâng tier được version hóa cùng capability matrix (mục 31, bước 2) và phải có evidence từ máy thật, không từ tài liệu marketing. Không có dòng máy nào được hiển thị Auto nếu chưa đạt 8.3.5.

Ngoài BIOS, tier cũng áp dụng cho display (DDC/CI theo hãng màn hình), peripheral và OEM utility, với cùng nguyên tắc: Observe trước, Write khi có API chính thức.

---

## 19. Bảo mật và an toàn hệ thống

### 19.1 Trust boundary

- UI không giữ quyền admin liên tục;
- privileged helper không nhận command shell tùy ý từ UI;
- chỉ nhận action ID, schema-valid parameters và plan hash;
- action được allowlist và ký/version hóa;
- IPC phải xác minh process/client và chống replay;
- log không chứa secret;
- package update phải được ký.

### 19.2 Phân loại rủi ro

| Mức | Ví dụ | Yêu cầu UX |
|---|---|---|
| Low | đổi refresh rate có timeout | xác nhận thông thường |
| Medium | enable Windows feature, service, network setting | preview + elevation + rollback |
| High | BitLocker, boot configuration, driver/firmware | cảnh báo mạnh + preflight + recovery plan |
| Critical | BIOS flashing/setting có thể gây boot failure | chỉ hỗ trợ vendor/model đã xác minh hoặc guided-only |

### 19.3 Dữ liệu nhạy cảm

- encryption at rest cho Vault/migration package;
- không thu thập password/token theo mặc định;
- support report có bước privacy review;
- telemetry là opt-in hoặc tối thiểu, có danh sách sự kiện rõ ràng;
- cloud sync không phải dependency để dùng các chức năng cốt lõi.

---

## 20. Mô hình dữ liệu cốt lõi

Các entity chính:

```text
Device
ConnectionPath
Capability
CapabilityEdge
Evidence
Setting
DesiredState
DriftRecord
Adapter
ActionDefinition
ActionExecution
ChangeSet
TransactionCheckpoint
Snapshot
KnownGoodCheckpoint
VerificationResult
ChangeEvent
RegressionCandidate
Profile
AppConfigManifest
PCBlueprint
BlueprintVersion
MigrationPackage
DiagnosticFinding
SupportReport
```

### 20.1 Thông tin cần lưu cho mỗi execution

- action/recipe version;
- timestamp và user approval;
- before value;
- requested after value;
- actual after value;
- commands/API adapter đã sử dụng ở mức an toàn để audit;
- exit/result code;
- verification evidence;
- restart/session ID;
- rollback result;
- warning/error có cấu trúc.

---

## 21. Information Architecture và giao diện

Navigation đề xuất:

```text
Overview
Hardware
BIOS & UEFI
Windows
Drivers & Firmware
Display
Network
Devices
Audio & Camera
Gaming
Developer
Power
Storage
Startup
Diagnostics
Repair & Recovery
Profiles
Desired State
Timeline
Blueprint & Migration
History
```

Change Basket luôn xuất hiện khi có pending changes:

```text
4 pending changes                         Review & Apply
```


### 21.1 Cross-cutting views

Ba màn hình không nên bị xem như module phụ:

**Desired State**

```text
Expected
Current
Drift
Reason / source if known
Restore action
```

**Timeline**

```text
Updates
Drivers
Firmware
Settings
Profiles
Crashes
Transactions
Rollbacks
```

**Blueprint**

```text
Current Blueprint
Versions
Export
Compare
Apply to this PC
Reconcile with another PC
```

Các màn hình này đọc dữ liệu từ nhiều module, vì vậy cần dùng common data model thay vì mỗi module lưu history riêng.

### 21.2 Search

Search chỉ cần hoạt động theo cách thông thường:

- tên setting;
- alias và từ khóa đồng nghĩa đã khai báo;
- category;
- hardware/device;
- module;
- status như `disabled`, `update available`, `restart required`.

Không cần NLP/AI trong phiên bản đầu.

### 21.3 Trạng thái hiển thị thống nhất

```text
Supported
Enabled / Disabled
Needs attention
Update available
Restart required
Guided only
Read-only
Unsupported
Unknown
```

`Unknown` phải được coi là trạng thái hợp lệ; app không được tự suy ra `Disabled` khi đọc thất bại.

### 21.4 Hai tầng giao diện: Simple và Advanced

Navigation 21 mục ở đầu mục 21 là **Advanced**. Người dùng phổ thông không mở app để duyệt module; họ mở vì máy có vấn đề hoặc muốn máy “sẵn sàng” cho một việc. Vì vậy mặc định là **Simple**:

```text
SIMPLE (mặc định)
┌───────────────────────────────────────────────┐
│ Máy của bạn: ASUS TUF B650 · Windows 11 Pro   │
│ Lần kiểm tra: 2 phút trước                    │
│                                                │
│ ● 2 điều đáng chú ý                            │
│   Màn hình đang chạy 60 Hz, hỗ trợ 165 Hz     │
│   Docker/WSL 2 chưa sẵn sàng (thiếu 3 bước)    │
│                                                │
│ Chuẩn bị máy cho…                              │
│   [Chơi game] [Lập trình] [Họp / Stream]        │
│                                                │
│ Vừa thay đổi (3)              Hoàn tác ▸       │
│                                                │
│ [Kiểm tra lại]              Advanced ▸         │
└───────────────────────────────────────────────┘
```

Simple chỉ có: findings từ Checkup, các Outcome, panel Vừa thay đổi, và một lối vào Advanced. Advanced chứa đầy đủ module, Desired State, Timeline, Blueprint. Cùng một Capability Graph và Transaction Engine bên dưới; chỉ khác cửa vào. Người dùng chọn tầng ở first-run và đổi được bất cứ lúc nào; app không tự đẩy người dùng sang Advanced.

### 21.5 First-run

1. Chào một câu, không tour 8 bước.
2. Scan read-only chạy ngay, có progress theo nhóm (phần cứng → Windows → firmware → thiết bị), tổng dưới 60 giây trên máy trung bình.
3. Kết quả đầu tiên là **Checkup**, không phải dashboard trống.
4. Nếu máy không có gì đáng chú ý: hiện rõ “Máy bạn đang ổn” với 3–4 dòng tóm tắt cấu hình đọc được và tier hỗ trợ của máy (mục 18.4). Trạng thái “không cần làm gì” là kết quả tốt và phải được thiết kế cẩn thận như trạng thái có lỗi.
5. Hỏi đúng hai điều: ngôn ngữ (đã đoán từ Windows locale, chỉ xác nhận) và có muốn app chạy nền để theo dõi drift không (mặc định **không**).
6. Không yêu cầu tài khoản, không hỏi telemetry ở first-run (telemetry opt-in nằm trong Settings).

### 21.6 Ngôn ngữ của một finding

Mọi finding và action hiển thị theo cấu trúc bốn dòng ở tầng Simple, mở rộng kỹ thuật ở tầng Advanced:

```text
Màn hình của bạn đang chạy 60 Hz nhưng hỗ trợ 165 Hz
Sửa xong: chuyển động mượt hơn rõ rệt khi kéo cửa sổ và chơi game
An toàn: tự quay lại 60 Hz sau 15 giây nếu bạn không xác nhận nhìn được
Không cần restart · khoảng 10 giây

[Sửa ngay]  [Thêm vào danh sách]  [Vì sao?]  Chi tiết kỹ thuật ▸
```

`Vì sao?` mở nguồn khuyến nghị (Microsoft/OEM/hardware vendor) và điều kiện không nên áp dụng. Copy do người viết tiếng bản ngữ soạn, không dịch máy từ tên setting.

### 21.7 Header “chi phí của plan” trong Change Basket

Trước nút Apply, Change Basket luôn hiện tổng chi phí mà người dùng phải trả, tính từ dữ liệu State Compiler đã có:

```text
5 thay đổi · 1 lần restart · ~4 phút · 1 bước bạn tự làm trong BIOS
2 thay đổi không tự hoàn tác được  ▸ xem

[Apply ngay]   [Apply và restart lúc 21:00]   [Apply, tôi sẽ restart sau]
```

Không có transaction phức tạp nào chỉ có một nút Apply.

### 21.8 Restart: gom, hoãn, lên lịch

- Mọi action cần restart trong cùng basket dùng chung **một** restart; app không bao giờ yêu cầu restart nhiều lần cho một plan nếu dependency cho phép gom.
- Ba lựa chọn cố định: restart ngay / restart lúc giờ chọn / tôi sẽ tự restart. Pending change tồn tại qua nhiều ngày, không hết hạn.
- Trước restart: liệt kê ứng dụng có tài liệu chưa lưu (qua Restart Manager API), cảnh báo laptop pin thấp, cảnh báo BitLocker (10.3).
- Sau restart: app tự mở lại đúng transaction, không cần người dùng tìm.
- Không bao giờ restart tự động mà không có xác nhận rõ ràng trong phiên hiện tại.

### 21.9 Panel “Vừa thay đổi” và Undo

Nằm ngay Overview (cả Simple và Advanced), không chôn trong History:

```text
Vừa thay đổi
  Hôm nay 14:02   Màn hình 60 → 165 Hz              [Hoàn tác]
  Hôm nay 13:58   WSL: Tắt → Bật                     [Hoàn tác]  (cần restart)
  Hôm qua         Default audio → HyperX             [Hoàn tác]
  Hôm qua         SVM Mode → Bật (bạn làm trong BIOS)  Hướng dẫn tắt ▸
```

Undo là một transaction ngược, đi qua đúng preview → apply → verify. Action không hoàn tác tự động được thì hiện hướng dẫn hoàn tác thay vì ẩn nút. Đây là thứ tạo cảm giác an toàn hơn mọi lời hứa về transaction; nó phải ở nơi dễ thấy nhất.

### 21.10 Người dùng không có quyền admin (Standard User mode)

Máy gia đình, máy công ty và máy trường học thường dùng tài khoản standard. App phải hoạt động có ý nghĩa mà không cần elevation:

- Scan, Checkup, Hardware Center, đọc trạng thái firmware, Timeline đọc, Support Report: **hoạt động đầy đủ**.
- Action cần admin: hiện rõ nhãn “Cần quyền quản trị” ngay trong finding, không đợi đến khi Apply mới báo lỗi UAC.
- Cho phép tạo **Request package**: một file plan đã ký kèm giải thích, để người có admin (IT, người nhà) review và apply trên chính máy đó. Đây cũng là workflow cho persona hỗ trợ kỹ thuật.
- Không bao giờ hướng dẫn người dùng cách vượt qua chính sách của tổ chức.

### 21.11 Đa ngôn ngữ (Localization)

Sản phẩm phục vụ người dùng phổ thông ở nhiều thị trường, nên i18n là yêu cầu kiến trúc từ v0.1, không phải bước dịch cuối.

**Kiến trúc**

- Toàn bộ chuỗi hiển thị nằm ngoài code (resource files theo locale, ICU MessageFormat để xử lý số nhiều, giới tính, thứ tự từ). Không có chuỗi hard-code trong logic.
- Recipe, guide data (8.3.3) và tên capability có `display_name` và `description` theo locale, tách khỏi `technical_id` bất biến. Ví dụ: `firmware.cpu.virtualization` → “Ảo hóa CPU” / “CPU virtualization” / “CPU-Virtualisierung”.
- Search index (21.2) chứa alias đa ngữ: người dùng gõ “ảo hóa”, “VT-x”, “SVM”, “virtualization” đều tới cùng capability.
- Evidence và log giữ giá trị gốc (tên setting trong BIOS bằng tiếng Anh của hãng), UI hiển thị bản dịch kèm tên gốc trong ngoặc khi cần người dùng tìm trong BIOS: *“SVM Mode (Ảo hóa AMD)”*.
- Đơn vị, ngày giờ, số theo locale; tên kỹ thuật chuẩn (Hz, MT/s, Gbps) không dịch.
- Support Report có thể xuất bằng ngôn ngữ của người hỗ trợ khác với ngôn ngữ của người dùng.

**Nguyên tắc dịch**

- Ưu tiên thuật ngữ Microsoft dùng trong bản Windows cùng ngôn ngữ, để người dùng nhận ra khi mở Settings của Windows.
- Không dịch tên riêng của tính năng vendor (Resizable BAR, XMP, EXPO, Secure Boot) — giữ nguyên, thêm giải thích ngắn.
- Copy tầng Simple (21.6) được viết lại bởi người bản ngữ, không dịch từng chữ.
- Mọi ngôn ngữ đều có bộ screenshot kiểm tra tràn chữ (tiếng Đức, tiếng Việt có dấu, tiếng Nhật) trong CI.

**Thứ tự ngôn ngữ đề xuất**

```text
v0.1   English, Tiếng Việt   (đội ngũ kiểm tra được chất lượng cả hai)
v0.2   Español, Português (BR), Deutsch, Français
v0.3   日本語, 한국어, 中文 (简体/繁體), Русский, Türkçe, Bahasa Indonesia, ไทย
sau    theo số người dùng và cộng đồng đóng góp; guide data BIOS có thể được dịch bởi cộng đồng qua cùng quy trình review với recipe
```

### 21.12 Portable / Support mode và Accessibility

**Portable mode**: một bản chạy từ USB, không cài, không service, chỉ Observe + Checkup + Support Report + xem Timeline có sẵn. Dành cho người sửa máy và cho chính người dùng khi máy khởi động được nhưng không ổn định. Đây là kênh lan truyền tự nhiên của sản phẩm trong cộng đồng kỹ thuật.

**Accessibility**: hỗ trợ screen reader (UI Automation), điều khiển hoàn toàn bằng bàn phím, high-contrast, cỡ chữ theo Windows, không truyền tải trạng thái chỉ bằng màu (luôn có icon + chữ). Countdown trong Safe Apply (22.2) phải có cách gia hạn cho người dùng chậm.

---

## 22. Các luồng UX tiêu biểu

### 22.1 Bật virtualization và WSL

1. User mở Developer Center.
2. App hiển thị CPU hỗ trợ virtualization nhưng firmware đang tắt.
3. User thêm `Virtualization`, `Virtual Machine Platform` và `WSL` vào basket.
4. App tính dependency và cho biết có một bước Guided trong BIOS.
5. User review rồi Apply.
6. App chạy BitLocker preflight, cho xem trước hướng dẫn đúng model (hoặc gửi sang điện thoại bằng QR), áp dụng các bước online phù hợp, lưu checkpoint và restart vào BIOS. Trên Dell/Lenovo/HP có adapter, bước firmware là Auto và không cần vào BIOS.
7. User bật SVM/VT-x theo hướng dẫn (chỉ với Guided).
8. Khi quay lại Windows, app tiếp tục workflow và đọc lại trạng thái thật để verify; nếu không đạt, đi nhánh 8.3.4 thay vì báo Failed.
9. Chỉ khi tất cả check đạt, trạng thái mới là Completed.

### 22.2 Sửa màn hình 165 Hz đang chạy 60 Hz

1. Display Center phát hiện rated/current refresh rate khác nhau.
2. User chọn 165 Hz.
3. App apply tạm thời và bắt đầu countdown.
4. User xác nhận hình ảnh bình thường.
5. App lưu state mới; nếu không xác nhận thì tự revert.

### 22.3 Chuẩn bị PC cho Valorant

1. Gaming Center chạy checklist versioned.
2. App hiển thị TPM đạt, Secure Boot chưa đạt, refresh rate đang đúng.
3. User chỉ chọn các mục muốn sửa.
4. App không tự động “optimize” các setting không bắt buộc.

### 22.4 Chuyển WSL khỏi ổ C

1. Storage Center hiển thị dung lượng WSL.
2. User chọn destination.
3. App kiểm tra dung lượng, distro state và điều kiện rollback.
4. App export/move/import bằng workflow được hỗ trợ.
5. App verify distro khởi động, filesystem đúng và default mapping còn hợp lệ.
6. Chỉ xóa bản cũ sau khi user xác nhận hoặc thời gian an toàn kết thúc.
### 22.5 Khôi phục sau drift

1. User đã đánh dấu một `Known Good` checkpoint.
2. Sau Windows/driver update, PC Control scan lại state.
3. App phát hiện refresh rate, default audio hoặc ReBAR thay đổi.
4. Timeline cho biết các thay đổi xảy ra quanh cùng một update/restart window.
5. User review từng drift thay vì app tự sửa.
6. State Compiler tạo restore transaction chỉ cho các action compatible và reversible.
7. Transaction Engine apply rồi verify từng state.
8. Known-good checkpoint cũ không bị ghi đè tự động.

### 22.6 “Máy bắt đầu lỗi từ hôm qua”

1. User mở Diagnostics và chọn symptom.
2. App chạy current health checks.
3. Timeline lấy các change event trong khoảng thời gian trước khi symptom bắt đầu.
4. Regression Intelligence xếp hạng candidate dựa trên thời gian, dependency, evidence và repeated pattern.
5. User mở diff hoặc rollback candidate.
6. Nếu rollback thành công và symptom biến mất qua verification phù hợp, evidence causal được tăng mức tin cậy.

### 22.7 Apply PC Blueprint lên máy mới

1. User import Blueprint.
2. App scan Capability Graph của máy mới.
3. State Compiler reconcile Blueprint với current state.
4. App phân loại item thành `transferable`, `adapted`, `skip`, `manual`.
5. User review difference plan.
6. App cài/khôi phục theo phase.
7. Sau restart, workflow resume và verify.
8. App tạo Blueprint version mới cho máy mới thay vì giả định hai máy giống nhau.


---

## 23. MVP đề xuất — v0.1

MVP không nên cố điều khiển toàn bộ PC. Mục tiêu là chứng minh năm giá trị:

1. scan đúng trạng thái máy;
2. Capability Graph biểu diễn đúng dependency của một workflow thật;
3. State Compiler tạo difference plan thay vì chạy preset cố định;
4. thay đổi có preview, transaction semantics và kiểm chứng;
5. workflow có thể tiếp tục qua restart và tạo history đủ để dùng cho Timeline sau này.

### 23.1 Phạm vi v0.1

#### A. Hardware inventory cơ bản

- CPU, GPU, RAM, motherboard, BIOS, storage, monitor;
- rated/current RAM speed khi đọc được;
- monitor supported/current refresh rate;
- virtualization support/state.

#### B. Windows Features

- WSL;
- Virtual Machine Platform;
- Windows Hypervisor Platform;
- Hyper-V;
- Windows Sandbox;
- OpenSSH;
- Developer Mode;
- Long Paths.

#### C. BIOS/UEFI capability đầu tiên

- **Observe** trên mọi máy: virtualization, Secure Boot, TPM, boot mode, RAM rated/current, ReBAR (khi driver báo), BIOS version/model — kèm evidence source và confidence;
- **Guided generic** (Tier 3) cho virtualization trên mọi máy: hướng dẫn theo vendor, QR sang điện thoại, restart to firmware UI, pending change, resume và verify sau boot, nhánh không-thành-công (8.3.4);
- **Guided verified** (Tier 2) cho ít nhất 2 board desktop trong lab với guide data theo model;
- **Auto** (Tier 1) cho **một** dòng Dell qua WMI chính thức — đủ để chứng minh contract vendor adapter, không mở rộng thêm trong v0.1;
- BitLocker preflight (10.3) bắt buộc trước mọi restart vào firmware.

#### D. Change Basket

- stage nhiều change;
- preview before/after;
- dependency ordering;
- elevation chỉ khi apply;
- restart checkpoint;
- verify từng action;
- history và rollback cho action được hỗ trợ.


#### E. State Compiler tối thiểu

Chỉ cần hỗ trợ một outcome đầu tiên:

```text
Docker / WSL 2 Ready
```

Compiler phải:

- đọc current state;
- bỏ qua capability đã đạt;
- tạo action cho phần còn thiếu;
- đánh dấu firmware step là Auto/Guided/Unsupported;
- sắp dependency;
- tạo restart boundary;
- verify outcome cuối.

#### F. Change Event Log nền tảng

Chưa cần full Regression Intelligence trong v0.1, nhưng mọi action và restart phải sinh event có schema thống nhất để không phải redesign History về sau.

Tối thiểu log:

- action before/after;
- transaction ID;
- restart ID;
- verification result;
- Windows feature changes do PC Control thực hiện;
- firmware state trước/sau khi đọc được.

#### G. PC Checkup ban đầu



- virtualization mismatch;
- RAM rated/current speed mismatch;
- monitor refresh rate mismatch;
- System Restore disabled;
- Windows feature dependency mismatch;
- BitLocker/Device Encryption đang bật mà chưa có recovery key được xác nhận (cảnh báo mềm, không phải lỗi).

#### H. Display refresh-rate fix với Safe Apply

Luồng 22.2 được đưa vào v0.1 dù nằm ngoài vertical slice Docker, vì: kỹ thuật đơn giản (display configuration API), không cần restart, là finding phổ biến nhất trên máy gamer và laptop mới, và là demo mà **người không phải developer cảm nhận được ngay**. Nó cũng là nơi thử Safe Apply (countdown + auto-revert) và panel Undo sớm nhất.

#### I. Nền tảng UX và i18n

- Simple/Advanced mode với Simple là mặc định (21.4);
- first-run theo 21.5; trạng thái “máy bạn đang ổn” được thiết kế đầy đủ;
- panel Vừa thay đổi / Undo trên Overview (21.9);
- header chi phí của plan và 3 lựa chọn restart (21.7, 21.8);
- string externalization + ICU + alias đa ngữ trong search; ship English và Tiếng Việt;
- Standard User mode ở mức: scan/checkup đầy đủ, nhãn “cần quyền quản trị” trên action.

### 23.2 Không đưa vào v0.1

- community plugin store;
- BIOS flashing phổ quát;
- full driver updater;
- fan curve/undervolt/overclock;
- cloud migration;
- hàng trăm repair scripts;
- peripheral adapter cho mọi hãng;
- AI/chatbot;
- vendor write adapter cho Lenovo/HP/ASUS/MSI (chỉ Dell, một dòng);
- Request package cho standard user (chỉ nhãn “cần quyền quản trị”);
- ngôn ngữ ngoài English/Tiếng Việt;
- Portable mode;
- automatic performance optimization;
- full Desired State auto-remediation;
- full Regression Intelligence;
- cross-device PC Blueprint restore;
- deep Hardware Path Intelligence ngoài một số proof-of-concept read-only.

### 23.3 Demo “wow moment” của v0.1

Demo lý tưởng:

```text
Scan PC
→ Capability Graph phát hiện Docker/WSL 2 chưa ready
→ State Compiler tính đúng phần còn thiếu trên máy này
→ user review difference plan
→ Change Basket tạo transaction
→ apply phần Windows phù hợp
→ restart vào BIOS với guided step nếu cần
→ quay lại Windows
→ transaction tự resume
→ verify outcome: WSL 2 ready
→ Timeline/History có đầy đủ before/after + restart correlation
```

Nếu workflow này chạy đáng tin cậy trên một tập hardware xác định, dự án đã chứng minh được core khác biệt.

---

## 24. Roadmap đề xuất

### Phase 0 — Technical spikes

- inventory Windows hardware và settings;
- test elevation/IPC model;
- test reboot resume;
- test restart to firmware UI;
- xác định dữ liệu BIOS nào đọc được trên các máy thử nghiệm;
- xây action contract và verification contract;
- xây schema Capability Graph;
- xây event schema dùng chung cho History/Timeline;
- prototype deterministic State Compiler cho một outcome.

### Phase 1 — v0.1 Core State Control

- Hardware Center tối thiểu;
- Windows Feature Center;
- firmware Observe layer trên mọi máy (Tier 3) + Guided generic + BitLocker preflight;
- Dell vendor adapter (Tier 1, một dòng) để chứng minh contract;
- Display refresh-rate fix với Safe Apply;
- Simple/Advanced mode, first-run, panel Undo, i18n foundation (EN/VI);
- Capability Graph cho Docker/WSL vertical slice;
- State Compiler tối thiểu;
- Change Basket + Transaction Engine;
- History, verify và rollback cơ bản;
- reboot/resume;
- PC Checkup.

### Phase 2 — Daily Use + Desired State

- Display Center;
- Power Center;
- Network Center cơ bản;
- Temporary Mode;
- PC Profiles phiên bản đầu;
- Known-good checkpoint;
- Desired State;
- Drift Detection read-only với UX im lặng/digest/accept-as-new-normal (13.4);
- restore selected low-risk drift;
- Tier 1: Lenovo, HP adapters; Tier 2: guide data cộng đồng cho ASUS/MSI/Gigabyte/ASRock;
- ngôn ngữ đợt 2 (ES, PT-BR, DE, FR);
- Request package cho Standard User.

### Phase 3 — Maintenance + Timeline

- driver/firmware inventory;
- Update Guard;
- PC Change Timeline;
- regression candidate ranking;
- Diagnostics workflows;
- Repair/Recovery Center;
- Support Report;
- Portable mode;
- ngôn ngữ đợt 3.

### Phase 4 — Hardware Intelligence

- USB connection path;
- display path;
- charger/power path khi đọc được;
- Port Advisor;
- hardware bottleneck explanation;
- thêm OEM/vendor adapters có evidence tốt.

### Phase 5 — PC Lifecycle / Blueprint

- App Settings Vault;
- Smart Storage Mover;
- PC Blueprint;
- blueprint version/diff;
- reinstall/new PC reconciliation;
- encrypted export/import;
- hardware-aware migration.

### Phase 6 — Ecosystem

- adapter SDK;
- signed recipe format;
- declarative desired-state/outcome format;
- community submission/review;
- vendor/OEM integrations;
- recipe/plugin store có permission model.

### 24.1 Nguyên tắc roadmap

Không được mở rộng bằng số lượng setting trước khi các core contract ổn định:

```text
Capability Graph
→ State Compiler
→ Transaction Engine
→ Verification
→ Event History
```

Một module mới chỉ đáng thêm nếu tận dụng được các core trên thay vì trở thành một utility độc lập sống bên cạnh hệ thống.

---

## 25. Tiêu chí thành công

### 25.1 Product metrics

- tỷ lệ scan hoàn thành không lỗi;
- tỷ lệ finding đúng được user xác nhận;
- tỷ lệ action verify thành công;
- tỷ lệ rollback thành công;
- tỷ lệ plan tiếp tục thành công sau restart;
- số lần user phải rời app để mở Settings/Control Panel/BIOS guide khác;
- thời gian hoàn thành workflow so với thao tác thủ công;
- tỷ lệ support report giải quyết được vấn đề mà không cần hỏi lại thông tin cơ bản.

Bổ sung v3:

- **Guided completion rate**: tỷ lệ Guided BIOS step được verify đạt sau boot lần đầu (mục tiêu ≥ 80% trên Tier 2, ≥ 65% trên Tier 3);
- **Time-to-first-value**: từ mở app lần đầu đến khi có finding có thể hành động (< 90 giây);
- **Undo usage without support**: người dùng tự hoàn tác thành công mà không tìm trợ giúp;
- **Drift acceptance vs restore**: tỷ lệ Accept-as-new-normal so với Restore, dùng để đo drift detection có hữu ích hay đang gây phiền;
- **Locale coverage**: % người dùng thấy UI bằng ngôn ngữ Windows của họ.

### 25.2 Reliability targets

- không báo thành công nếu verification thất bại;
- không đánh dấu `Disabled` khi trạng thái là `Unknown`;
- change history không mất qua crash/restart;
- tất cả privileged action đều có audit record;
- tất cả action mức High/Critical có preflight và recovery policy;
- cùng state snapshot + cùng rule versions phải tạo cùng compiler plan.

### 25.3 North-star outcome

Người dùng có thể chọn một outcome nhiều bước, để PC Control hiểu current state, tính đúng phần còn thiếu, thực thi qua restart, verify kết quả thật và duy trì được lịch sử đủ để giải thích các thay đổi về sau.

### 25.4 Moat metrics

- tỷ lệ outcome được compiler giải quyết mà không cần user tự tìm hướng dẫn ngoài app;
- tỷ lệ plan không chứa action thừa vì capability đã đạt;
- tỷ lệ transaction resume thành công sau reboot;
- tỷ lệ drift có nguồn thay đổi xác định được;
- tỷ lệ regression candidate được user xác nhận hữu ích;
- tỷ lệ Blueprint item được reconcile đúng trên hardware khác;
- tỷ lệ hardware bottleneck explanation có evidence end-to-end;
- số workflow thực sự cross-layer thay vì chỉ là Windows toggle.

---

## 26. Khác biệt cạnh tranh

### 26.1 Không cạnh tranh bằng số lượng toggle

Các nhóm công cụ hiện có thường mạnh ở một phần:

| Nhóm công cụ hiện có | Thường làm tốt | Khoảng trống PC Control nhắm tới |
|---|---|---|
| Windows Settings/Control Panel | Setting chính thức | Phân tán, thiếu dependency và workflow xuyên lớp |
| Windows tweak/debloat tools | Nhiều tweak, preset, install/fix nhanh | Thường thiên về script/preset, không phải desired-state compiler |
| OEM control center | Điều khiển sâu hardware/firmware của một hãng | Vendor lock-in, thiếu cross-system graph |
| GPU vendor apps | Driver/display/GPU settings | Chỉ hiểu subsystem GPU |
| Hardware monitoring tools | Sensor và inventory sâu | Ít action orchestration và outcome reasoning |
| Backup/migration tools | File hoặc image hệ thống | Không biểu diễn đầy đủ “cách PC hoạt động” |
| Enterprise configuration | Desired state/policy ở managed devices | Không phải consumer experience cross-layer, hardware-aware |

### 26.2 Các tầng khác biệt

```text
7. PC Blueprint / Lifecycle
6. Timeline / Regression Intelligence
5. Desired State / Drift Detection
4. Transaction Engine
3. PC State Compiler
2. PC Capability Graph
1. Adapter / Evidence Layer
```

Tầng 1 có thể bị copy tương đối dễ.

Tầng 2–4 cần data model, compatibility, verification và execution reliability.

Tầng 5–7 tạo giá trị tích lũy theo thời gian: PC Control biết known-good state, lịch sử thay đổi và Blueprint của user.

### 26.3 Moat dự kiến

- **cross-layer:** Windows + firmware + driver + hardware + app state;
- **hardware-aware:** current state được đọc từ capability thực, không chỉ preset;
- **state compiler:** desired outcome → difference plan riêng cho từng PC;
- **transaction-like changes:** stage, dependency order, restart/resume, partial completion, verify;
- **desired state:** biết máy lệch khỏi expected/known-good state ở đâu;
- **timeline intelligence:** biết những thay đổi nào xảy ra trước regression;
- **hardware path:** hiểu bottleneck qua connection chain;
- **blueprint:** bảo toàn environment và cách chiếc PC hoạt động qua reinstall/migration;
- **safety-first:** least privilege, evidence, snapshot, rollback và recovery;
- **local-first:** giá trị cốt lõi không phụ thuộc cloud.

### 26.4 Câu kiểm tra định vị

Một feature mới chỉ củng cố định vị nếu trả lời được ít nhất một câu:

1. Nó giúp PC Control hiểu state tốt hơn?
2. Nó làm graph/dependency sâu hơn?
3. Nó giúp compiler tính plan chính xác hơn?
4. Nó làm transaction an toàn/kiểm chứng được hơn?
5. Nó giúp biết máy đã drift hoặc regression ở đâu?
6. Nó giúp bảo toàn/reconcile Blueprint tốt hơn?

Nếu không, feature đó có nguy cơ biến PC Control thành một bộ utility tổng hợp và nên bị loại hoặc để plugin xử lý.

---

## 27. Rủi ro chính và cách kiểm soát

### 27.1 BIOS không có API ghi thống nhất

**Rủi ro:** Khả năng *ghi* setting firmware phụ thuộc vendor; trên desktop board tiêu dùng gần như không có. Kỳ vọng sai từ đội ngũ hoặc marketing (“app tự bật BIOS cho bạn”) sẽ dẫn đến hoặc hứa hẹn không giữ được, hoặc dùng kỹ thuật ghi UEFI variable nguy hiểm.  
**Kiểm soát:** tách Observe/Write (8.3); Guided là đường mặc định được đầu tư đầy đủ; Hardware Support Tiers công khai (18.4); Auto chỉ qua API chính thức và tiêu chí 8.3.5; cấm ghi `Setup` variable theo offset.

### 27.2 Một setting có thể khác nhau giữa các Windows build

**Rủi ro:** registry/API/command thay đổi.  
**Kiểm soát:** action versioning; OS build constraints; detection trước execution; signed metadata update.

### 27.3 Quyền admin làm tăng attack surface

**Rủi ro:** UI hoặc plugin độc hại gửi lệnh nguy hiểm.  
**Kiểm soát:** helper nhỏ, allowlist action, schema validation, plan hash, signed package, không nhận shell command tùy ý.

### 27.4 Rollback không phải lúc nào cũng khả thi

**Rủi ro:** firmware flash, boot setting hoặc update nhất định không thể tự quay lại.  
**Kiểm soát:** phân loại risk; thông báo rõ; guided-only; chuẩn bị recovery key/media; không gắn nhãn reversible nếu chưa chứng minh.

### 27.5 Recommendation sai tạo mất niềm tin

**Rủi ro:** Ví dụ XMP/EXPO không phải lúc nào cũng ổn định.  
**Kiểm soát:** phân biệt fact và recommendation; hiển thị nguồn/rủi ro; không tự apply; có confidence/evidence.

### 27.6 Phạm vi sản phẩm quá lớn

**Rủi ro:** cố hỗ trợ mọi hãng và mọi chức năng sẽ không thể hoàn thiện.  
**Kiểm soát:** MVP chỉ tập trung core; publish capability matrix; mở rộng theo adapter; ưu tiên một số hardware test matrix cụ thể.


### 27.7 State Compiler tạo plan “đúng kỹ thuật nhưng sai ý user”

**Rủi ro:** Outcome được mô tả quá rộng hoặc rule không đủ context.  
**Kiểm soát:** outcome contract cụ thể; preview difference; không auto-apply; deterministic plan; giải thích vì sao từng action được thêm.

### 27.8 Drift Detection trở thành công cụ “đánh nhau” với user hoặc app khác

**Rủi ro:** PC Control liên tục khôi phục một setting mà user/OEM/app khác cố tình thay đổi.  
**Kiểm soát:** read-only mặc định; phân loại initiator; grace period; explicit pin; không auto-remediate setting high-risk.

### 27.9 Regression correlation bị hiểu nhầm là causal diagnosis

**Rủi ro:** User tin một update chắc chắn gây lỗi chỉ vì xảy ra gần nhau.  
**Kiểm soát:** confidence vocabulary; phân biệt temporal correlation với verified cause; chỉ tăng causal evidence khi rollback + verification hỗ trợ.

### 27.10 Blueprint restore sai trên hardware khác

**Rủi ro:** Setting phụ thuộc GPU/mainboard/display cũ không phù hợp máy mới.  
**Kiểm soát:** reconcile qua Capability Graph; classify transferable/adapted/skipped/manual; không replay raw registry dump.

### 27.11 Hardware Path không đọc được toàn bộ chain

**Rủi ro:** Cable/dock/adapter không expose telemetry.  
**Kiểm soát:** `Unknown` là first-class state; chỉ kết luận bottleneck khi evidence đủ; cho user bổ sung metadata thủ công nhưng phải đánh dấu nguồn.

### 27.12 BitLocker recovery lock sau thay đổi firmware

**Rủi ro:** Người dùng đổi Secure Boot/TPM/boot mode theo hướng dẫn, boot lại gặp màn hình đòi recovery key và không biết key ở đâu; họ đổ lỗi cho app dù app không ghi gì.  
**Kiểm soát:** preflight 10.3 bắt buộc, không cho Apply khi chưa xác nhận key hoặc suspend; ghi event riêng; hướng dẫn phục hồi bằng ngôn ngữ đời thường có sẵn offline (QR).

### 27.13 Nhầm giữa “không ghi được” và “không làm được gì”

**Rủi ro:** Đội ngũ hoặc người dùng đánh giá thấp Guided vì nghĩ không ghi được BIOS thì không có giá trị, dẫn đến cắt bỏ đầu tư vào guide data và verify.  
**Kiểm soát:** nguyên tắc 6.9; KPI riêng cho Guided completion rate (25.1); coi guide data là sản phẩm được version hóa và review như recipe.

### 27.14 Dịch thuật làm sai nghĩa kỹ thuật

**Rủi ro:** Tên setting dịch khác Windows/BIOS khiến người dùng không tìm được; dịch máy tạo câu vô nghĩa ở tầng Simple.  
**Kiểm soát:** giữ `technical_id` và tên gốc; ưu tiên thuật ngữ Microsoft theo locale; copy tầng Simple do người bản ngữ viết; screenshot test tràn chữ trong CI.

---

## 28. Nguyên tắc phát triển và kiểm thử

- mỗi action mới phải có detect, preflight, apply, verify và rollback declaration;
- unit test cho planning/dependency;
- integration test trên Windows VM cho Windows features;
- hardware lab/test matrix cho firmware, display, USB và OEM adapter;
- fault injection: mất điện giả lập, process crash, reboot giữa workflow, command timeout;
- mọi lỗi phải để workflow ở trạng thái có thể hiểu và tiếp tục hoặc rollback;
- recipe/adapter compatibility phải được version hóa theo OS build và device;
- beta đầu tiên chỉ bật action ghi trên hardware đã được xác minh;
- unknown hardware vẫn cho phép inventory/read-only, không cho phép hành động rủi ro;
- golden tests cho State Compiler: cùng snapshot + cùng rules phải tạo cùng plan;
- property tests cho dependency ordering và conflict detection;
- event replay tests cho Timeline;
- drift tests phải phân biệt user-initiated với external drift khi có evidence;
- blueprint reconciliation tests trên hardware fingerprint khác;
- không thêm auto-remediation nếu chưa có verification + rollback policy rõ ràng.

---

## 29. Các quyết định đã chốt

1. Dự án là **PC state/control layer**, không phải BIOS manager hoặc Windows tweaker đơn thuần.
2. Không làm Intent Engine và không xây sản phẩm theo hướng AI-first.
3. Search là search setting/module/hardware thông thường.
4. **PC Capability Graph** là source model cho dependency và evidence.
5. **PC State Compiler** biến desired state/outcome thành difference plan riêng cho từng PC.
6. Signature UX là **Change Basket**, nhưng signature architecture là **Transaction Engine**: preview → apply → restart/resume → verify → undo/partial recovery.
7. BIOS/UEFI tách thành **Observe** (bắt buộc trên mọi máy) và **Write** (chỉ qua API vendor chính thức). **Guided là đường mặc định** cho desktop board và laptop tiêu dùng; Auto là nâng cấp theo tier. Verify sau boot chạy như nhau ở cả hai chế độ.
8. Không làm “Optimize PC” mù hoặc debloat hàng loạt.
9. Driver/firmware chỉ dùng nguồn chính thức và có Update Guard.
10. **Desired State & Drift Detection** là pillar dài hạn; auto-remediation mặc định không bật.
11. **PC Change Timeline** là history model thống nhất, không phải log phụ của từng module.
12. Regression Intelligence chỉ biểu diễn evidence/confidence, không khẳng định nhân quả thiếu căn cứ.
13. **Hardware Path Intelligence** mở rộng inventory thành khả năng giải thích bottleneck.
14. **PC Blueprint** là representation của environment/cách PC hoạt động, không phải disk image.
15. Local-first, least privilege, transparency và evidence-first là yêu cầu kiến trúc.
16. MVP vẫn ưu tiên virtualization + Windows features + hardware findings + State Compiler + reboot-resume workflow.
17. Các moat mới phải được thiết kế data contract từ sớm nhưng triển khai đầy đủ theo phase, không nhồi vào v0.1.
18. Không bao giờ ghi trực tiếp vào UEFI `Setup` variable theo offset để đổi BIOS setting, dù cộng đồng có tool làm việc này.
19. Thứ tự vendor write adapter: **Dell → Lenovo → HP**; desktop board (ASUS/MSI/Gigabyte/ASRock) ở Tier 2 Guided Verified cho đến khi vendor có API chính thức.
20. BitLocker/Device Encryption preflight là bắt buộc cho mọi thay đổi firmware/boot, không có ngoại lệ.
21. Simple mode là mặc định; Advanced là lựa chọn. Ngôn ngữ đời thường trước, tên kỹ thuật sau.
22. i18n là yêu cầu kiến trúc từ v0.1; ship English và Tiếng Việt cùng lúc.
23. Undo phải nằm trên Overview; Drift im lặng mặc định và có Accept-as-new-normal.


---

## 30. Các quyết định còn mở

- tên thương hiệu chính thức;
- stack desktop cuối cùng: C#/.NET native hay React/Tauri + native core;
- nhóm motherboard/laptop đầu tiên được hỗ trợ;
- phạm vi telemetry mặc định;
- cơ chế phân phối signed adapters/recipes;
- mô hình open source, source-available hay commercial;
- Vault chỉ local hay có encrypted sync;
- marketplace có cho phép community code hay chỉ declarative recipe;
- chính sách license/partnership cho vendor APIs;
- Windows 10 có được hỗ trợ hay chỉ Windows 11;
- desired state được lưu theo machine, profile hay có lớp global/template;
- retention policy cho Timeline và event storage;
- mức evidence tối thiểu để một regression candidate được hiển thị;
- Blueprint format có public/open spec hay proprietary;
- hardware path nào đủ telemetry để support chính thức trong từng phase;
- guide data BIOS theo model: ai được đóng góp, quy trình review, có trả thưởng cho cộng đồng không;
- có làm companion web page/QR offline hay app điện thoại tối giản cho Guided step;
- ngôn ngữ đợt 2 chọn theo thị trường hay theo cộng đồng đóng góp;
- Request package cho standard user có cần ký bởi người apply không.

---

## 31. Việc nên làm tiếp theo

### Bước 1 — Chốt vertical slice duy nhất

Workflow đầu tiên:

> **Prepare this PC for WSL 2 / Docker: detect → compile plan → apply → survive restart → verify.**

Không bắt đầu bằng dashboard hàng trăm setting.

### Bước 2 — Lập capability matrix

Test tối thiểu trên:

- Intel desktop;
- AMD desktop;
- một laptop Dell/Lenovo/HP;
- một motherboard ASUS/MSI/Gigabyte;
- Windows 11 Home và Pro;
- ít nhất một máy có BitLocker/Device Encryption đang bật;
- ít nhất một máy dùng tài khoản standard.

Ghi rõ từng capability:

```text
observe: yes / partial / no
evidence source
confidence: high / medium / low
write mode: auto / guided-verified / guided-generic / read-only / unsupported
verification method
rollback method
guide data: per-model / per-vendor / none
```

Kết quả matrix quyết định tier của từng dòng máy (18.4) và được công khai trong app.

### Bước 3 — Chốt Capability Graph schema

Xây node/edge/evidence contract trước khi UI phụ thuộc vào dữ liệu ad-hoc.

Prototype graph chỉ cần đủ cho:

```text
CPU
→ firmware virtualization
→ VMP
→ WSL
→ WSL 2 readiness
→ Docker readiness
```

### Bước 4 — Prototype State Compiler

Input:

```text
StateSnapshot
DesiredOutcome
GraphVersion
RuleVersion
```

Output:

```text
Difference
CandidateActions
OrderedPlan
RestartBoundaries
Risks
Manual/GuidedSteps
VerificationPlan
```

Viết golden test cho nhiều machine state khác nhau.

### Bước 5 — Prototype Transaction Engine

Xây contract cho:

- detect;
- preflight;
- plan;
- elevate;
- apply;
- checkpoint;
- restart/resume;
- verify;
- partial completion;
- rollback.

### Bước 6 — Prototype Change Basket UI

Tập trung vào:

- outcome;
- before/after diff;
- dependency;
- reason từng action tồn tại;
- risk/restart/reversible;
- Guided step;
- trạng thái execution riêng từng action.

### Bước 6b — UX one-pager và hallway test

Trước khi code UI, viết một trang UX cho v0.1 gồm: first-run, Checkup card theo 21.6, Change Basket header 21.7, màn hình Guided BIOS (trước restart / QR / sau boot / nhánh không thành công), panel Undo, trạng thái “máy bạn đang ổn”.

Thử với 3–5 người **không phải developer** bằng mockup giấy hoặc Figma, ít nhất một người dùng Tiếng Việt và một người dùng English. Câu hỏi cần trả lời: họ hiểu máy mình cần gì không, họ dám bấm Apply không, họ biết cách hoàn tác không, họ làm được bước BIOS chỉ với hướng dẫn trên điện thoại không.

### Bước 7 — Chốt Event/Timeline schema ngay trong v0.1

Chưa làm UI Timeline đầy đủ nhưng mọi transaction phải tạo event normalized từ đầu.

Nếu không làm bước này, về sau Drift/Regression sẽ phải reconstruct history từ log không đồng nhất.

### Bước 8 — Threat model

Chốt:

- IPC;
- privileged helper;
- action allowlist;
- plan hash;
- signing;
- replay protection;
- log redaction;
- adapter update trust.

### Bước 9 — Sau khi vertical slice ổn mới mở rộng

Ưu tiên tiếp:

```text
Display
→ Desired State / Drift
→ Update Timeline
→ Diagnostics
→ Hardware Path
→ Blueprint
```

Không ưu tiên tăng số lượng tweak.


---

## 32. Kết luận

PC Control không cần thắng bằng số lượng tweak, số GB dọn được hoặc số nút toggle.

Dự án có cơ hội khác biệt nếu làm tốt một việc khó hơn:

> **Biến chiếc PC từ một tập hợp hệ thống cấu hình rời rạc thành một hệ thống trạng thái có thể hiểu, lập kế hoạch, thay đổi, kiểm chứng, theo dõi và tái tạo.**

Core sản phẩm là:

```text
Discover the real machine
        ↓
Build a capability graph
        ↓
Understand current state
        ↓
Choose a desired outcome/state
        ↓
Compile the difference
        ↓
Review a transaction plan
        ↓
Apply with least privilege
        ↓
Survive restart
        ↓
Verify the real result
        ↓
Record what changed
        ↓
Detect future drift/regression
        ↓
Preserve the environment as a Blueprint
```

Vertical slice đầu tiên vẫn là Virtualization/WSL/Docker readiness vì nó ép hệ thống chứng minh gần như toàn bộ core:

- hardware detection;
- firmware capability;
- Windows dependency;
- State Compiler;
- Change Basket;
- privileged execution;
- Guided mode;
- reboot/resume;
- verification;
- history/event correlation.

Sau khi vertical slice này đáng tin cậy, cùng architecture có thể mở rộng sang display, networking, power, gaming, diagnostics, Desired State, Timeline, Hardware Path Intelligence và PC Blueprint mà không làm mất định vị.

### Product identity

PC Control không phải:

```text
A bigger tweak app
```

Mà hướng tới:

```text
A state and control layer for the personal computer
```

### Short product statement

> **Know your PC. Shape its state. Verify every change. Keep it working your way.**

### Working tagline

> **Configure it. Use it. Fix it. Keep it. Move it — from one place.**
