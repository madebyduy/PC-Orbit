# PC Orbit — Nghiên cứu chức năng chuyên sâu giúp người dùng tự sử dụng, cài đặt và cứu hộ PC

**Ngày chốt nguồn:** 04/09/2026  
**Đối tượng:** đội sản phẩm/kỹ thuật PC Orbit; người dùng Windows phổ thông tại Việt Nam  
**Phạm vi:** PC cá nhân Windows 10/11, máy bộ/laptop OEM và desktop tự lắp; từ lúc chọn linh kiện, lắp/cài máy, vận hành, chẩn đoán, BIOS/UEFI, phục hồi, chuyển máy đến thanh lý.  
**Trạng thái:** Proposed — nghiên cứu định hướng sản phẩm, chưa phải đặc tả triển khai cuối cùng.

## Tóm tắt điều hành

PC Orbit không nên trở thành “một bảng có thật nhiều nút chỉnh” hay một chatbot đưa lệnh ngẫu nhiên. Khoảng trống lớn nhất là một **hệ thống điều phối tự phục vụ an toàn**: biết máy nào đang được thao tác, biết trạng thái thật, chọn phương án ít phá hủy nhất, chuẩn bị đường lui trước khi thay đổi, tiếp tục qua lần khởi động lại và xác minh bằng nguồn đọc độc lập.

Nghiên cứu cho thấy lõi hiện có của PC Orbit — capability graph, plan, transaction, verify, rollback, trạng thái Unknown và BIOS guided — là đúng hướng. Phần nên đầu tư tiếp không phải thêm hàng trăm setting, mà là sáu luồng P0:

1. **Recovery Readiness & Emergency Kit:** đo máy có thực sự cứu được trước khi sự cố xảy ra.
2. **Install/Reinstall Concierge:** chuẩn bị và nối liền trước cài → trong cài → sau cài, không xóa nhầm ổ và không để người dùng mất driver/mã hóa/kích hoạt.
3. **Root-cause Timeline:** trả lời “điều gì vừa thay đổi trước khi máy hỏng?” bằng bằng chứng và mức tin cậy.
4. **Boot & Recovery Orchestrator:** luôn ưu tiên đường phục hồi ít gián đoạn nhất, từ repair/reinstall-in-place đến WinRE rồi mới reset/clean install.
5. **Driver Steward:** quản lý nguồn gốc, driver dự phòng, cài từng nhóm, theo dõi hậu kiểm và rollback.
6. **BIOS/UEFI Safe Navigator + Firmware Guardian:** hướng dẫn đúng model/phiên bản, khóa an toàn BitLocker/nguồn điện/mật khẩu, dùng API OEM chính thức nếu có, và xác minh sau boot.

Ba yếu tố tạo khác biệt thực sự là: **model-specific thay vì hướng dẫn chung**, **evidence-first thay vì đoán lỗi**, và **recovery-first thay vì chỉ tối ưu khi máy đang hoạt động**.

Lớp mở rộng nên bắt đầu bằng **Smart Cleanup có phân vùng an toàn và hoàn tác**, **Startup/Background Controller**, **Performance Root-cause Profiler**, **App Repair**, **Update Regression Guard**, **Freeze/Hang Recorder** và **BIOS Baseline & Diff**.

## 1. Vấn đề cần giải quyết

Người dùng thường không thất bại vì thiếu một lệnh; họ thất bại vì không biết lệnh đó có đúng với máy mình, có làm mất dữ liệu, có cần khóa khôi phục BitLocker, có cần driver mạng/lưu trữ trước khi cài lại, hoặc làm thế nào quay về trạng thái cũ. BIOS còn khó hơn vì tên menu, khả năng hỗ trợ, cơ chế cập nhật và phục hồi khác nhau theo hãng, dòng máy và phiên bản firmware.

Các công cụ Windows/OEM hiện có rất mạnh nhưng rời rạc: WinRE, Recovery Drive, SetupDiag, PnPUtil, DISM, powercfg, WHEA, Windows Backup, winget, công cụ BIOS của Dell/HP/Lenovo, QVL của nhà sản xuất bo mạch. PC Orbit nên là lớp nối chúng thành một hành trình có ngữ cảnh, không phải thay thế hay bọc lại mọi công cụ.

## 2. Nguyên tắc sản phẩm bắt buộc

1. **Outcome-first:** người dùng chọn “cài lại mà giữ dữ liệu”, “bật Secure Boot để chơi game”, “sửa lỗi màn hình đen”, không chọn registry key hay cmdlet.
2. **Evidence-first:** mọi kết luận phải chỉ rõ nguồn dữ liệu, thời điểm, độ mới và mức tin cậy; Unknown là kết quả hợp lệ.
3. **Least-disruptive-first:** thử phương án ít mất dữ liệu/ứng dụng nhất trước. Microsoft hiện cũng xếp reinstall bằng Windows Update, gỡ update và restore trước reset/clean install.
4. **Recovery-before-change:** trước thay đổi có rủi ro phải có khóa BitLocker, nguồn điện, ảnh chụp trạng thái, đường khôi phục và tiêu chí dừng.
5. **Model-specific:** BIOS, QVL, CPU support, đèn debug, cổng và recovery phải bám đúng model/revision; hướng dẫn chung phải gắn nhãn độ tin cậy thấp hơn.
6. **One reversible experiment:** mỗi lần chỉ thay đổi một biến khi chẩn đoán; tự động quay lại khi mất màn hình/mạng hoặc hết thời gian.
7. **Independent verification:** không coi exit code thành công là hoàn tất; đọc lại bằng nguồn khác và kiểm tra outcome.
8. **No secret-by-default:** không đưa mật khẩu Wi-Fi, recovery key, token, dump bộ nhớ hay dữ liệu cá nhân vào log/bundle mặc định.
9. **AI là lớp giải thích, không là authority:** AI có thể tóm tắt bằng chứng và chọn câu hỏi tiếp theo; không tự phát minh BIOS path, URL driver hay lệnh ghi firmware.

## 3. Kiến trúc năng lực đề xuất

Luồng chuẩn:

`Ý định người dùng → Nhận diện thiết bị/model → Evidence graph → Risk & policy gate → Plan có preview → Adapter chính thức/guided step → Checkpoint/reboot resume → Verify độc lập → Timeline/rollback/handoff`

### Các khối chính

| Khối | Trách nhiệm | Khi dữ liệu thiếu hoặc lỗi |
|---|---|---|
| Experience surfaces | Giao diện Windows, chế độ điện thoại/QR khi ở BIOS hoặc WinRE, hướng dẫn bằng hình | Chuyển sang checklist read-only, không cho thao tác nguy hiểm |
| Device identity & evidence graph | Model, revision, service tag cục bộ, hardware IDs, firmware/OS state, nguồn và độ mới | Trạng thái Unknown, yêu cầu người dùng xác nhận nhãn/model |
| Outcome compiler | Chuyển mục tiêu thành dependency, xung đột, preflight và kế hoạch | Không biên dịch nếu thiếu điều kiện an toàn |
| Safety policy engine | BitLocker, nguồn, pin, dung lượng, backup, quyền, mật khẩu BIOS, mức hỗ trợ adapter | Fail closed đối với ghi firmware/xóa ổ; cho phép quan sát |
| Execution adapters | Windows API/CLI allowlist, OEM API, UEFI capsule, guided-only adapter | Hạ cấp Auto → Guided → Read-only → Unsupported |
| Recovery coordinator | Checkpoint, reboot resume, timeout auto-revert, WinRE/USB handoff, rollback | Giữ trạng thái durable; hiển thị đường cứu hộ thay vì lặp vô hạn |
| Verification & timeline | Đọc lại trạng thái, kiểm tra outcome, ghi thay đổi/sự kiện/chẩn đoán | Gắn nhãn “chưa xác minh”; không báo thành công giả |
| Signed knowledge packs | Manual/model map, QVL/CPU/BIOS floor, nguồn tải chính thức, playbook có version | Hết hạn thì chỉ quan sát; yêu cầu cập nhật pack |

## 4. Danh mục chức năng chuyên sâu

### 4.1 Recovery Readiness & Emergency Kit — P0

**Việc người dùng cần:** “Nếu ngày mai máy không khởi động, tôi có tự cứu được không?”

Chức năng:

- Chấm điểm khả năng phục hồi theo từng điều kiện có thể kiểm chứng: WinRE đang bật và trỏ đúng phân vùng; recovery drive có tồn tại và còn mới; khóa BitLocker đã được người dùng xác nhận truy cập; đủ dung lượng; backup dữ liệu có thời điểm gần đây; driver mạng/lưu trữ cần thiết có bản sao; OEM BIOS recovery có áp dụng cho model; máy có thể boot USB/UEFI.
- Tạo **Emergency Kit** theo máy: manifest phần cứng, driver bootstrap, liên kết recovery OEM, mã QR mở hướng dẫn trên điện thoại, thông tin edition/activation, checksum media, và checklist không chứa secret.
- Nhắc tạo lại Recovery Drive định kỳ; Microsoft khuyến nghị tạo lại hằng năm vì recovery media không phải bản sao dữ liệu cá nhân.
- Chạy “recovery drill” không phá hủy: kiểm tra WinRE, đường vào Advanced startup, mạng trong WinRE khi được hỗ trợ, và xác nhận khóa khôi phục — không giả lập hỏng boot trên máy thật đại trà.

**Không làm:** sao chép recovery key vào log; tuyên bố “đã backup” khi chưa thử đọc; biến Recovery Drive thành backup dữ liệu.

### 4.2 Install/Reinstall Concierge — P0

**Việc người dùng cần:** “Cài lại Windows nhưng không mất nhầm dữ liệu, không mất mạng/driver và biết phải làm gì sau lần boot đầu.”

Hành trình năm pha:

1. **Preflight:** phân loại mục tiêu (repair, reinstall giữ apps/files/settings, reset giữ file, clean install), edition/license, UEFI/GPT, BitLocker, dung lượng, nguồn, backup, tài khoản và thời gian gián đoạn.
2. **Migration manifest:** file quan trọng, app inventory, Wi-Fi/accessibility/settings, winget export, WSL export, license cần tự xử lý, app setting được hỗ trợ; đánh dấu những gì Windows Backup không khôi phục hoàn chỉnh.
3. **Media & disk safety:** chỉ tới nguồn Microsoft/OEM chính thức; kiểm tra checksum/chữ ký khi khả dụng; nhận diện ổ bằng model/serial/dung lượng/sơ đồ phân vùng; buộc xác nhận vật lý trước thao tác xóa.
4. **Install handoff:** chuẩn bị USB, driver lưu trữ/mạng, hướng dẫn UEFI boot; không nhúng mật khẩu hoặc recovery key vào unattend; không tự chọn/xóa partition khi nhận diện còn mơ hồ.
5. **First-boot resume:** agent tiếp tục bằng mã kế hoạch: cập nhật Windows, driver theo policy, khôi phục app/distro/settings, kiểm tra activation, encryption, Device Manager, mạng, âm thanh, display và tạo baseline mới.

Ưu tiên phương án ít phá hủy: trên Windows 11 phù hợp, reinstall current version qua Windows Update có thể giữ file, app và settings; clean install chỉ là nhánh cuối khi có lý do rõ.

### 4.3 Root-cause Timeline — P0

**Việc người dùng cần:** “Máy bắt đầu lỗi sau thay đổi nào, và bằng chứng ở đâu?”

- Hợp nhất Windows Update, cài/gỡ app, driver/PnP, firmware, WER/minidump, WHEA, Setup logs, thay đổi power/display/network và phiên giao dịch PC Orbit lên một timeline.
- Dùng **change-point window**: so sánh khoảng bình thường gần nhất với thời điểm lỗi đầu tiên; xếp hạng giả thuyết theo quan hệ thời gian, subsystem match, mức lặp lại và khả năng đảo ngược.
- Mỗi giả thuyết có: bằng chứng ủng hộ, bằng chứng chống, độ tin cậy, phép thử an toàn tiếp theo và điều kiện dừng.
- SetupDiag là bộ phân tích chuyên biệt cho lỗi nâng cấp/cài Windows; WHEA cho bằng chứng lỗi phần cứng; minidump hỗ trợ phân tích bugcheck. Đây là input, không phải “phán quyết duy nhất”.

**Rào chắn:** dùng từ “có tương quan/khả năng” thay vì khẳng định nguyên nhân; không tải dump lên cloud nếu chưa có đồng ý riêng.

### 4.4 Boot & Recovery Orchestrator — P0

**Việc người dùng cần:** “Máy không vào Windows; hãy dẫn tôi theo đường ít mất mát nhất.”

Decision ladder đề xuất:

1. Nếu Windows còn vào được: built-in troubleshooter → repair/reinstall qua Windows Update → gỡ update/driver vừa gây lỗi → Point-in-time restore/System Restore.
2. Nếu boot lỗi lặp lại: WinRE/Startup Repair; trên Windows 11 24H2 build phù hợp, nhận diện Quick Machine Recovery và kiểm tra khả năng mạng/driver trong WinRE.
3. Nếu nghi driver: Safe Mode, rollback hoặc offline servicing có kiểm soát; không gỡ boot-critical driver mù quáng.
4. Nếu nghi file hệ thống: DISM rồi SFC theo đúng ngữ cảnh; không chạy như “thuốc chữa bách bệnh”.
5. Reset giữ file → recovery media OEM/generic → clean install chỉ sau khi hiện rõ những gì sẽ mất.

PC Orbit lưu kế hoạch và bằng chứng ở vùng bền vững để tiếp tục sau reboot/WinRE, nhưng không cài công cụ tùy ý vào WinRE nếu làm tăng bề mặt tấn công hoặc phá hỗ trợ OEM.

### 4.5 Driver Steward — P0

**Việc người dùng cần:** “Cài đúng driver cho đúng thiết bị, và quay lại được nếu driver mới gây lỗi.”

- Inventory theo hardware/compatible IDs, provider, signer, rank, date/version, package, trạng thái thiết bị và problem code.
- Chính sách nguồn: Windows Update trước; OEM/nhà sản xuất chính thức khi cần; tuyệt đối không dùng trang driver tổng hợp.
- Xuất **Driver Vault** bằng công cụ Windows chính thức trước cài lại/thay đổi; tách bootstrap (network/storage/chipset) khỏi package tùy chọn.
- Cài theo subsystem và một giao dịch/lần; checkpoint; reboot khi cần; quan sát Device Manager, WER, network/display/audio và rollback window.
- Nhận diện driver bị chặn bởi memory integrity/vulnerable driver policy; đề xuất cập nhật/thay thế trước khi tắt bảo vệ.

**Không làm:** “Update all drivers” chỉ vì version lớn hơn; ép unsigned driver; xóa driver boot-critical khỏi offline image mà không có xác nhận model và recovery path.

### 4.6 BIOS/UEFI Safe Navigator & Firmware Guardian — P0

**Việc người dùng cần:** “Thay đổi/cập nhật BIOS đúng cách mà không bị khóa BitLocker hoặc làm máy không boot.”

- Nhận diện system/board model, revision và BIOS version; map tên tính năng chuẩn sang tên/menu đúng firmware; hiển thị ảnh/đường dẫn từng bước và QR để dùng điện thoại khi màn hình PC ở BIOS.
- Bốn mức hỗ trợ: **Auto** chỉ khi có API OEM/Windows firmware update hợp lệ và đã kiểm thử; **Guided** khi cần người dùng thao tác; **Read-only** khi chỉ đọc được; **Unsupported** khi không đủ bằng chứng.
- Preflight: AC/pin, BitLocker recovery access và suspension nếu cần, BIOS/admin password, checksum/chữ ký, đúng model, release notes, reboot count, trạng thái boot, đường recovery OEM.
- Cập nhật qua Windows UEFI firmware platform/UpdateCapsule hoặc công cụ OEM chính thức; không ghi raw NVRAM/UEFI variable cho capability chưa xác minh.
- Sau reboot: đọc lại version/settings/outcome; kiểm tra mã lần thử cập nhật nếu nền tảng cung cấp; resume BitLocker; ghi timeline.
- Theo dõi readiness cho chuyển đổi chứng chỉ Secure Boot 2023 vì chứng chỉ 2011 bắt đầu hết hạn từ tháng 6/2026; chỉ đưa hướng dẫn phù hợp với Windows/OEM hiện tại.

### 4.7 New Build & No-POST Copilot — P1

**Việc người dùng cần:** “Tôi tự lắp/nâng cấp máy nhưng không biết vì sao bấm nguồn không lên hoặc không có hình.”

- **Before purchase:** socket/chipset, CPU support và BIOS floor, RAM QVL/kit, M.2/SATA lane sharing, đầu cấp nguồn CPU/GPU, PSU/case/cooler clearance khi có dữ liệu chính thức.
- **Assembly map:** lấy sơ đồ header/slot từ manual đúng model; đánh dấu CPU_PWR, ATX, front panel, RAM priority, GPU power, monitor path.
- **POST triage:** ghi nhận fan/LED/beep/Q-LED/Q-code; hướng dẫn minimal POST; một DIMM đúng slot; iGPU/dGPU; reseat; clear CMOS đúng manual; kiểm tra BIOS Flashback và yêu cầu USB/file naming theo OEM.
- **Phone mode:** vì PC có thể không hiển thị, toàn bộ guide phải mở được trên điện thoại bằng QR/case code và hoạt động offline sau khi tải pack.

Nguồn hỗ trợ của MSI/ASUS/ASRock/AMD đều cho thấy no-display là cây quyết định phần cứng/model, không phải một lệnh Windows. Đây là lý do chức năng này cần knowledge pack theo bo mạch.

### 4.8 Hardware Health & Degradation — P1

**Việc người dùng cần:** “Linh kiện nào đang xuống cấp hay chỉ là lỗi phần mềm?”

- Storage: SMART/NVMe health, temperature, wear, media/error counters, unsafe shutdowns và xu hướng theo thời gian.
- Hardware errors: WHEA corrected/nonfatal/fatal theo component và tần suất.
- Battery: design/full-charge capacity, cycle count khi có; sleep drain và Modern Standby.
- Power/thermal: throttling, sleepstudy/energy report, wake blockers; cảnh báo theo baseline của chính máy thay vì một “health score” giả chính xác.
- Confidence model: phân biệt “không có lỗi”, “telemetry không được hỗ trợ” và “chưa quan sát đủ lâu”.

### 4.9 Privacy-safe Support Bundle & Human Handoff — P1

**Việc người dùng cần:** “Gửi đủ dữ liệu cho người giúp mà không gửi nhầm thông tin riêng tư.”

- Bundle theo sự cố: summary dễ đọc + phụ lục kỹ thuật + timeline + exact model/OS/build/driver/firmware + các test đã chạy.
- Preview và redact mặc định: username, đường dẫn file, IP/MAC, SSID, serial/service tag, lịch sử trình duyệt, token, recovery key; dump bộ nhớ là opt-in riêng vì có thể chứa nội dung người dùng.
- Tạo case code để người hỗ trợ mở đúng luồng; hỗ trợ Quick Assist nhưng bắt buộc nhắc chỉ kết nối với người đáng tin, hiển thị rõ quyền control và log phiên.

### 4.10 Security Compatibility Planner — P1

**Việc người dùng cần:** “Bật bảo mật/đáp ứng game hoặc Windows 11 mà không làm thiết bị ngừng hoạt động.”

- Outcome graph cho TPM, UEFI, Secure Boot, BitLocker, memory integrity, vulnerable driver blocklist, virtualization và yêu cầu ứng dụng/game.
- Kiểm tra driver/firmware/partition style trước; lên kế hoạch MBR→GPT nếu hợp lệ; chuẩn bị recovery; chỉ bật protection sau khi phụ thuộc đã sẵn sàng.
- Không áp enterprise security baseline máy móc cho người dùng gia đình; chính Microsoft yêu cầu cân nhắc rủi ro vận hành và tương thích.
- Tách “đang tắt”, “phần cứng không hỗ trợ”, “có thể bật nhưng bị chặn bởi driver”, “cần firmware update” và “không xác định”.

### 4.11 Upgrade Compatibility Planner — P2

**Việc người dùng cần:** “Nâng CPU/RAM/SSD/GPU có chạy thật không, và cần cập nhật gì trước khi tháo máy?”

- Compatibility graph: socket/chipset/CPU support list, BIOS floor, QVL memory, slot/lane sharing, PCIe generation, physical/power connector constraints, OS/driver/firmware prerequisites.
- Kế hoạch thứ tự: cập nhật BIOS với CPU cũ khi cần → lưu recovery → shutdown → lắp → minimal POST → verify → bật profile hiệu năng sau.
- Mỗi kết luận phải có URL nguồn OEM và ngày truy xuất; QVL là “đã được hãng kiểm tra”, không phải danh sách duy nhất có thể hoạt động.

### 4.12 Connection Path & Port Advisor — P2

**Việc người dùng cần:** “Tại sao cắm cùng thiết bị mà cổng/dock/cáp khác lại chậm, không sạc hoặc mất màn hình?”

- Vẽ đường USB controller → hub/dock → device; descriptor, tốc độ/power negotiated và slow-charge status khi UCSI cung cấp.
- Vẽ display path/connector/mode/scaling/orientation; ghép với dock/adapter/cable knowledge.
- Đề xuất A/B test cụ thể: đổi đúng một mắt xích, đo lại, tự quay setting display nếu mất tín hiệu.

### 4.13 Migration Blueprint — P2

**Việc người dùng cần:** “Sang PC mới và biết chính xác thứ gì đã/chưa được khôi phục.”

- Hợp nhất Windows Backup (folder/settings/app list), winget, WSL export/import, app-settings vault, peripheral profiles và activation checklist.
- Restore dạng reconciliation: desired vs observed; app nào cài được, license nào cần người dùng đăng nhập, setting nào không tương thích hardware mới.
- Mã hóa artifact nhạy cảm; secrets không vào blueprint; có dry-run và report hậu kiểm.

### 4.14 Device Retirement Wizard — P2

**Việc người dùng cần:** “Bán/tặng/bỏ máy mà dữ liệu không còn và không khóa nhầm tài khoản của mình.”

- Xác nhận backup/restore, sign-out/unlink account, kiểm tra encryption và recovery, reset/clean theo mục tiêu.
- Nói rõ “clean data” của Reset this PC là hướng người tiêu dùng và không phải tiêu chuẩn xóa dữ liệu cho chính phủ/ngành; dữ liệu nhạy cảm cần phương pháp sanitize/validate theo NIST SP 800-88 Rev.2 và khả năng thiết bị.
- Xuất biên bản thao tác/verification nhưng không tuyên bố chứng nhận nếu không có công cụ/xác minh phù hợp.

## 5. Ma trận ưu tiên

Điểm dưới đây là **ước lượng chuyên gia**, không phải số liệu thị trường. Thang 1–5; `Ưu tiên = 30% tác động + 25% giảm rủi ro + 20% độ phủ + 15% khả thi với lõi hiện tại + 10% khác biệt`. Cần hiệu chỉnh bằng phỏng vấn và telemetry đồng ý của người dùng.

| Hạng | Hệ thống | Tác động | Giảm rủi ro | Độ phủ | Khả thi | Khác biệt | Điểm/5 | Pha |
|---:|---|---:|---:|---:|---:|---:|---:|---|
| 1 | Recovery Readiness & Emergency Kit | 5 | 5 | 5 | 4 | 5 | 4.8 | P0 |
| 2 | Install/Reinstall Concierge | 5 | 5 | 5 | 4 | 4 | 4.7 | P0 |
| 3 | Root-cause Timeline | 5 | 4 | 5 | 4 | 5 | 4.6 | P0 |
| 4 | Boot & Recovery Orchestrator | 5 | 5 | 4 | 3 | 5 | 4.5 | P0 |
| 5 | Driver Steward | 5 | 5 | 5 | 4 | 3 | 4.5 | P0 |
| 6 | BIOS/UEFI Navigator + Firmware Guardian | 5 | 5 | 4 | 3 | 5 | 4.4 | P0 |
| 7 | Support Bundle & Handoff | 4 | 5 | 5 | 4 | 4 | 4.4 | P1 |
| 8 | Hardware Health & Degradation | 4 | 4 | 5 | 4 | 4 | 4.2 | P1 |
| 9 | New Build & No-POST Copilot | 5 | 4 | 3 | 3 | 5 | 4.1 | P1 |
| 10 | Security Compatibility Planner | 4 | 5 | 4 | 3 | 4 | 4.1 | P1 |
| 11 | Migration Blueprint | 4 | 4 | 4 | 3 | 4 | 3.8 | P2 |
| 12 | Upgrade Compatibility Planner | 4 | 4 | 3 | 3 | 5 | 3.8 | P2 |
| 13 | Connection Path & Port Advisor | 4 | 3 | 4 | 3 | 4 | 3.6 | P2 |
| 14 | Device Retirement Wizard | 3 | 5 | 3 | 4 | 3 | 3.6 | P2 |

## 6. Lộ trình khuyến nghị

### M0 — Nền tảng bằng chứng (0–8 tuần)

- Chuẩn hóa `DeviceIdentity`, `Evidence`, `RiskGate`, `RecoveryAsset`, `Hypothesis`, `SupportBundleManifest`.
- Signed knowledge-pack schema, expiry, OEM/source URL, supported model/revision/version.
- Privacy classification và redaction pipeline.
- Exit: mọi capability P0 biểu diễn được Unknown/source/freshness/confidence; plan hash và durable resume đã có test.

### M1 — Read-only P0 (2–4 tháng)

- Recovery Readiness scan; WinRE/BitLocker/recovery media/driver bootstrap checks.
- Install/Reinstall preflight + migration manifest; chưa xóa ổ hay flash BIOS.
- Root-cause timeline với SetupDiag/WHEA/PnP/Update/WER; driver inventory/vault.
- BIOS guide cho tập model nhỏ, chỉ Observe/Guided.
- Exit: 95% scan hoàn tất không side effect; bundle preview không rò secret trong test corpus; 100% kết luận có source/confidence.

### M2 — Giao dịch an toàn (4–7 tháng)

- Driver update/rollback theo subsystem.
- Recovery ladder khi Windows còn boot; reboot-resume; display/network auto-revert.
- Firmware update pilot chỉ với 1 OEM API/Windows capsule path đã kiểm chứng.
- Support handoff + phone mode.
- Exit: kill/reboot test không làm mất trạng thái; verify độc lập; BitLocker/power gates chặn đúng; rollback drill đạt ngưỡng.

### M3 — Ngoài hệ điều hành (7–12 tháng)

- WinRE handoff/offline diagnosis có giới hạn.
- No-POST copilot cho 10–20 motherboard phổ biến; knowledge packs được review.
- Hardware health trends và Security Compatibility Planner.
- Exit: model mismatch không bao giờ đi vào hướng dẫn ghi; mỗi playbook có test phần cứng thật hoặc bị gắn Guided/read-only.

### M4 — Mở rộng vòng đời (sau 12 tháng)

- Upgrade planner, port path, migration reconciliation, retirement wizard.
- Mở rộng OEM/model theo telemetry opt-in và nhu cầu thị trường Việt Nam.

## 7. Chỉ số thành công và launch gates

| Chỉ số | Mục tiêu ban đầu | Gate |
|---|---:|---|
| Tỷ lệ kế hoạch có đường recovery trước side effect rủi ro | 100% | Bắt buộc |
| Kết luận có source + freshness + confidence | 100% | Bắt buộc |
| False-success sau verify | <0,5% trong test/pilot | Bắt buộc |
| Resume thành công sau reboot/crash | ≥99,5% giao dịch được hỗ trợ | Bắt buộc |
| Rò secret/PII trong support bundle mặc định | 0 trong corpus kiểm thử | Bắt buộc |
| Model mismatch dẫn tới firmware write | 0 | Bắt buộc |
| Người dùng hoàn tất luồng P0 không cần terminal | ≥85% trong usability test | Khuyến nghị |
| Thời gian tìm nguyên nhân sơ bộ | giảm ≥50% so với baseline hỗ trợ | Khuyến nghị |
| Tỷ lệ rollback/recovery drill thành công | ≥95% trên ma trận hỗ trợ | Bắt buộc cho Auto |

## 8. Những lựa chọn không nên làm

| Lựa chọn | Vì sao hấp dẫn | Vì sao không chọn |
|---|---|---|
| “AI tự sửa mọi lỗi” | Trải nghiệm có vẻ đơn giản | Không có authority/ground truth; nguy cơ bịa model, lệnh, nguyên nhân và side effect |
| Universal BIOS writer | Tạo cảm giác bao phủ rộng | Rủi ro brick/BitLocker; OEM support và rollback khác nhau; chỉ nên dùng API chính thức đã kiểm thử |
| One-click optimize/update all | Dễ marketing | Không có outcome rõ, tạo nhiều biến cùng lúc, khó quy nguyên nhân/rollback |
| Kho hàng trăm repair scripts | Ra tính năng nhanh | Thiếu trạng thái, dependency, verify và provenance; dễ trở thành “random fixes” |
| Luôn cloud-first | Dễ cập nhật tri thức | Máy hỏng có thể mất mạng; dữ liệu chẩn đoán/dump nhạy cảm; cần offline pack và local-first |

## 9. Giới hạn nghiên cứu

- Chưa có phỏng vấn định tính/định lượng với người dùng Việt Nam, cửa hàng lắp máy hoặc kỹ thuật viên; ma trận ưu tiên là suy luận từ tài liệu kỹ thuật và hiện trạng PC Orbit.
- Tài liệu OEM thay đổi theo model, khu vực và BIOS revision; mọi knowledge pack phải có ngày hết hạn và quy trình tái xác minh.
- Quick Machine Recovery, Point-in-time restore và Secure Boot certificate transition là khả năng/phạm vi đang thay đổi trong 2026; phải feature-detect tại runtime, không chỉ dựa vào edition label.
- Một số telemetry (NVMe health, WHEA detail, battery cycle, fan/thermal) không có trên mọi thiết bị; Unknown không được quy thành “ổn”.
- Báo cáo không thay thế thử nghiệm trên phần cứng thật, threat modeling, pháp lý/quyền riêng tư hoặc nghiên cứu usability trước khi Auto mode ra mắt.

## 10. Claim-to-source ledger

| ID | Claim dùng trong đề xuất | Nguồn chính | Cách sử dụng |
|---|---|---|---|
| C01 | Windows có thang recovery từ ít gián đoạn đến reset/clean install; reinstall qua Windows Update có thể giữ file/apps/settings | [Microsoft — Recovery options in Windows](https://support.microsoft.com/en-us/windows/experience/backup-recovery/recovery-options-in-windows) | Cơ sở cho least-disruptive-first và Recovery Orchestrator |
| C02 | Quick Machine Recovery dùng WinRE có mạng để tìm remediation qua Windows Update; là best-effort và yêu cầu build phù hợp | [Microsoft Learn — Quick Machine Recovery](https://learn.microsoft.com/en-us/windows/configuration/quick-machine-recovery/) | Feature-detect, không hứa chữa mọi boot failure |
| C03 | Point-in-time restore có thể phục hồi toàn hệ thống gần đây, cần WinRE/BitLocker key và có thể mất thay đổi sau restore point | [Microsoft Support — Point-in-time restore](https://support.microsoft.com/en-us/windows/experience/backup-recovery/point-time-restore-for-windows) | Readiness check và cảnh báo tác động |
| C04 | Recovery Drive không chứa file cá nhân và nên tạo lại định kỳ | [Microsoft Support — Recovery Drive](https://support.microsoft.com/en-us/windows/experience/backup-recovery/recovery-drive) | Không đánh đồng recovery media với data backup |
| C05 | WinRE là nền tảng sửa OS không boot và có thể tùy biến driver/tool có kiểm soát | [Microsoft Learn — Windows RE technical reference](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/windows-recovery-environment--windows-re--technical-reference?view=windows-11) | Handoff/out-of-OS boundary |
| C06 | WinPE có thể cứu dữ liệu và thêm driver mạng/lưu trữ nhưng không phải OS đa dụng | [Microsoft Learn — WinPE introduction](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/winpe-intro?view=windows-11) | Giới hạn rescue environment |
| C07 | SetupDiag phân tích log Windows Setup, chạy online/offline và tìm rule match cho lỗi nâng cấp | [Microsoft Learn — SetupDiag](https://learn.microsoft.com/en-us/windows/deployment/upgrade/setupdiag) | Root-cause input cho install/upgrade |
| C08 | BitLocker có thể vào recovery khi firmware/TPM/boot order thay đổi; cần suspend đúng cho update nhiều reboot | [Microsoft Learn — BitLocker FAQ](https://learn.microsoft.com/en-us/windows/security/operating-system-security/data-protection/bitlocker/faq), [known issues](https://learn.microsoft.com/en-us/troubleshoot/windows-client/windows-security/bitlocker-recovery-known-issues) | Hard gate cho BIOS/firmware/boot-plan |
| C09 | Windows hỗ trợ firmware update qua UEFI UpdateCapsule/ESRT và signed driver package | [Microsoft Learn — Windows UEFI firmware update platform](https://learn.microsoft.com/en-us/windows-hardware/drivers/bringup/windows-uefi-firmware-update-platform) | Chỉ Auto trên đường chính thức |
| C10 | UEFI FMP có get/check/set image; rollback là platform-specific | [UEFI Specification 2.11 — Firmware Update](https://uefi.org/specs/UEFI/2.11/23_Firmware_Update_and_Reporting.html) | Không giả định universal rollback |
| C11 | Dell/Lenovo/HP có công cụ BIOS chính thức nhưng model/password/workflow khác nhau | [Dell Command Configure](https://www.dell.com/support/manuals/en-us/command-configure/dcc_ug_5.x/manage-bios-settings?guid=guid-5d7ed65b-4b6a-4236-9c91-b16f0e5b20e1), [Lenovo WMI Guide](https://docs.lenovocdrt.com/ref/bios/wmi/wmi_guide/), [HP CMSL](https://h10032.www1.hp.com/ctg/Manual/c06696094.pdf) | Adapter matrix Auto/Guided/Read-only |
| C12 | PnPUtil hỗ trợ enumerate/export driver, device problems và restart/disable device | [Microsoft Learn — PnPUtil syntax](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax) | Driver inventory/vault/executor allowlist |
| C13 | Windows chọn driver theo match/rank/signature/date/version; Windows Update là nguồn khuyến nghị | [Microsoft Learn — driver selection](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/overview-of-the-driver-selection-process), [Microsoft Support — update drivers](https://support.microsoft.com/en-us/windows/update-drivers-through-device-manager-in-windows-ec62f46c-ff14-c91d-eead-d7126dc1f7b6) | Không dùng quy tắc “version cao nhất” hoặc nguồn lạ |
| C14 | Offline driver servicing có thể thêm driver boot/network/storage; gỡ boot-critical driver có thể làm image không boot | [Microsoft Learn — offline drivers](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/add-and-remove-drivers-to-an-offline-windows-image?view=windows-11) | Gate nghiêm cho WinRE/offline repair |
| C15 | WHEA ghi hardware error records/events; dump hỗ trợ phân tích bugcheck nhưng có thể chứa dữ liệu cá nhân | [Microsoft Learn — WHEA events](https://learn.microsoft.com/en-us/windows-hardware/drivers/whea/whea-hardware-error-events), [WinDbg dump analysis](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/analyzing-a-kernel-mode-dump-file-with-windbg), [Windows diagnostic data privacy](https://learn.microsoft.com/en-us/windows/privacy/configure-windows-diagnostic-data-in-your-organization) | Evidence correlation và consent/redaction |
| C16 | Powercfg có energy/batteryreport/sleepstudy/wake tools | [Microsoft Learn — powercfg options](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options) | Hardware health/power diagnostics |
| C17 | Windows Backup, winget và WSL có các mảnh export/restore riêng, không tạo một blueprint đầy đủ | [Microsoft Support — Windows Backup](https://support.microsoft.com/en-us/windows/experience/backup-recovery/back-up-and-restore-with-windows-backup), [winget export](https://learn.microsoft.com/en-au/windows/package-manager/winget/export), [WSL commands](https://learn.microsoft.com/en-us/windows/wsl/basic-commands) | Lý do cần migration reconciliation |
| C18 | CPU mới có thể cần BIOS phù hợp; QVL/CPU support và no-POST steps là theo bo mạch | [AMD — BIOS updates](https://www.amd.com/en/resources/support-articles/faqs/cpu-99.html), [ASUS — QVL](https://www.asus.com/support/FAQ/1043883), [MSI — no display](https://www.msi.com/support/technical_details/MB_Boot_No_Display) | Upgrade/No-POST model-specific packs |
| C19 | USB/UCSI và display APIs cho topology, negotiated power/speed và active display paths | [Microsoft Learn — UCSI](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/ucsi), [USBView](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/usbview), [QueryDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig) | Connection Path Advisor |
| C20 | Secure Boot kiểm tra chữ ký chuỗi boot; chứng chỉ 2011 bắt đầu hết hạn từ 06/2026 | [Microsoft Learn — Secure boot](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/oem-secure-boot) | Lifecycle/security readiness có tính thời điểm |
| C21 | Memory integrity/vulnerable driver blocklist là lớp bảo vệ; tắt đi làm giảm bảo mật | [Microsoft Support — Device security](https://support.microsoft.com/en-us/windows/security/windows-security/device-security-in-the-windows-security-app) | Driver compatibility trước khi tắt protection |
| C22 | Enterprise security baseline phải cân nhắc tương thích và tác động vận hành | [Microsoft Learn — Windows security baselines](https://learn.microsoft.com/en-us/windows/security/operating-system-security/device-management/windows-security-configuration-framework/windows-security-baselines) | Không áp baseline mù quáng cho consumer |
| C23 | Quick Assist yêu cầu người dùng cho phép và có cảnh báo lừa đảo | [Microsoft Support — Quick Assist](https://support.microsoft.com/en-us/windows/apps/solve-pc-problems-remotely-using-quick-assist) | Human handoff có consent/scam guard |
| C24 | Reset clean-data không đạt chuẩn xóa dữ liệu chính phủ/ngành; NIST SP 800-88 Rev.2 là hướng dẫn hiện hành từ 09/2025 | [Microsoft Support — Reset this PC](https://support.microsoft.com/en-us/windows/experience/backup-recovery/reset-your-pc), [NIST SP 800-88 Rev.2](https://csrc.nist.gov/pubs/sp/800/88/r2/final) | Retirement wizard và tuyên bố giới hạn |

## 11. Khuyến nghị quyết định

Phê duyệt hướng **“PC self-service safety system”** và triển khai M0→M2 trước. Mốc đầu tiên không cần tự flash BIOS hay tự sửa WinRE: hãy phát hành một vertical slice read-only gồm Recovery Readiness + Install Preflight + Driver Vault + Root-cause Timeline, gắn với lõi plan/verify hiện có. Sau khi chứng minh được model identity, privacy redaction, durable resume và rollback drill, mới mở Auto cho từng adapter OEM/Windows đã kiểm thử.

Điểm quyết định cứng: **không quảng bá “hỗ trợ BIOS” theo số lượng setting; hãy quảng bá theo số model/workflow có đường xác minh và cứu hộ đã thử thật.** Đó là khác biệt giữa một công cụ hướng dẫn đáng tin và một bộ mẹo có thể làm hỏng máy.

## 12. Phụ lục — Hệ sinh thái chức năng mở rộng

> **Đã đối chiếu với spec.** Phụ lục này là kết quả nghiên cứu, không phải backlog. Quyết định
> nhận gì / hoãn gì / bỏ hẳn gì nằm ở [ADR 0004](../adr/0004-research-backlog-reconciliation.md).
> Danh sách dưới đây đã áp dụng quyết định đó: các module bị bác bỏ được ghi lại trong ADR kèm lý
> do, và không còn xuất hiện ở đây như thể chúng đang chờ tới lượt.

### 12.1 Smart Cleanup — dọn rác có bằng chứng — **hoãn**

Smart Cleanup phải lập bản đồ dung lượng, giải thích lý do xóa, ước lượng dung lượng thu hồi, cho
xem trước và giữ đường hoàn tác:

- **Tự động an toàn:** temporary/cache hết hạn, thumbnail cache, Recycle Bin quá hạn và log cũ theo retention đã biết.
- **Cần người dùng duyệt:** Downloads, file lớn/cũ, ứng dụng ít dùng, cloud offline files, crash dumps và cache game/browser.
- **Không xóa trực tiếp:** WinSxS, DriverStore, recovery partition, restore point, registry và app data không rõ.
- Bổ sung duplicate finder theo hash, incomplete download/installer, uninstall leftovers theo manifest/service/task/startup, exact reclaim estimate, quarantine 7–30 ngày và hậu kiểm sau dọn.
- Không có registry cleaner, RAM cleaner hoặc xóa mù Windows.old/WinSxS/DriverStore. Xóa Windows.old làm mất khả năng quay lại phiên bản Windows trước.

**Lý do hoãn:** `UndoPlanner` hoàn tác bằng cách khôi phục *before-value* mà mỗi bước đã đọc. Một
file đã xóa không có before-value. Muốn làm đúng phải xây quarantine có retention + reclaim đo
thật + restore từng mục — một primitive chưa tồn tại. Xem ADR 0004.

Nguồn: [Microsoft — Free up drive space](https://support.microsoft.com/en-us/windows/experience/storage-filemanagement/free-up-drive-space-in-windows), [Storage settings](https://support.microsoft.com/en-us/windows/experience/storage-filemanagement/storage-settings-in-windows).

### 12.2 Trạng thái các module bổ sung

**Đã triển khai** — không cần primitive mới:

| Module | Nơi triển khai |
|---|---|
| Security Compatibility Planner | `workload.windows11-ready`, `outcome.windows11-ready`, `Windows11ReadinessRule` |
| BIOS Baseline & Diff | `Core/Compare/SnapshotDiff`, `pco diff` |
| Recovery Readiness & Emergency Kit | các node `recovery.*`, `RecoveryReadinessRule` |
| Root-cause Timeline (nguồn ngoài) | `IChangeSource`, `WindowsChangeSources`, `pco timeline` |
| Update Regression Guard | gộp vào `pco timeline` |
| Startup & Background Controller (chỉ inventory) | `IStartupInventory`, `pco startup` |

**Hoãn** — chờ một primitive chưa có, đã ghi rõ trong ADR 0004: Smart Cleanup; tắt startup entry;
Boot & Recovery Orchestrator; Driver Inventory + Rollback; Install/Reinstall Concierge; Migration
Blueprint; Privacy-safe Support Bundle.

**Gộp** — nhiều tiêu đề, một hệ thống:

- *Why is my PC slow* + *Performance Root-cause Profiler* + *Freeze/Hang Recorder* + *Gaming & Creator Stability Lab* → một subsystem ETW.
- *App Repair* + *Broken Uninstaller Rescue* + *Application Conflict Detector* + *Default Apps Repair* + *Runtime & Dependency Doctor* → một subsystem application state.
- *Post-malware Recovery* + *Suspicious Persistence Scanner* + *Ransomware Readiness* → chỉ giữ persistence **inventory**.

**Không phải module** — là yêu cầu xuyên suốt sản phẩm: Family/Senior Mode (spec 21.4 simple mode);
Offline Phone Companion (đã có: `data/guides/*.guide.json` + `docs/ux/mockup/PhoneGuide.dc.html`);
Second-opinion Mode (cách hypothesis ranking phải hành xử).

**Đã bác bỏ** — lý do đầy đủ trong ADR 0004: Noise Source Finder; Dust & Maintenance Planner;
Energy Cost Tracker; File Organization Assistant; Download Reputation & Signature;
What can my PC do? Advisor.

### 12.3 Thứ tự discovery còn lại

Sau khi phần "đã triển khai" ở trên đã vào code, phần discovery còn lại theo thứ tự:

1. Quarantine primitive — điều kiện tiên quyết cho Smart Cleanup.
2. `ActionParameters` cho identifier do người dùng chọn — điều kiện tiên quyết cho tắt startup entry.
3. Freeze/Hang Recorder (ETW).
4. Driver Inventory + Rollback.
5. Partition Safety + SSD Migration.
6. Backup Integrity Monitor.

Các module phải dùng chung `MachineIdentity`, `Evidence`, preflight, checkpoint, verify, rollback và
event log; không biến thành các nút kỹ thuật rời rạc.
