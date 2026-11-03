// macOS Bluetooth helper for Timular Tracker. .NET has no CoreBluetooth binding, so the app runs this
// and reads its stdout. Usage: zei-ble connect | zei-ble wake
//   connect: finds + connects to the ZEI, then prints  CONNECTED / SIDE <n> / DISCONNECTED / ERROR <msg>
//   wake:    listens for the ZEI advertising again, prints WOKE and exits
// It exits when stdin closes, so it never outlives the app.
import CoreBluetooth
import Foundation

let serviceUUID = CBUUID(string: "C7E70010-C847-11E6-8175-8C89A55D403C")
let orientationUUID = CBUUID(string: "C7E70012-C847-11E6-8175-8C89A55D403C")

func out(_ s: String) { print(s); fflush(stdout) }
func die(_ code: Int32, _ s: String? = nil) -> Never { if let s = s { out(s) }; exit(code) }

final class Helper: NSObject, CBCentralManagerDelegate, CBPeripheralDelegate {
    let wake: Bool
    var central: CBCentralManager!
    var peripheral: CBPeripheral?
    var orientation: CBCharacteristic?
    var poll: Timer?

    init(wake: Bool) {
        self.wake = wake
        super.init()
        central = CBCentralManager(delegate: self, queue: nil)
    }

    func isZei(_ name: String?, _ adv: [String: Any]) -> Bool {
        let n = (adv[CBAdvertisementDataLocalNameKey] as? String) ?? name ?? ""
        if n.range(of: "Timeular", options: .caseInsensitive) != nil || n.range(of: "ZEI", options: .caseInsensitive) != nil { return true }
        return ((adv[CBAdvertisementDataServiceUUIDsKey] as? [CBUUID]) ?? []).contains(serviceUUID)
    }

    func centralManagerDidUpdateState(_ c: CBCentralManager) {
        switch c.state {
        case .poweredOn: start()
        case .unauthorized: die(1, "ERROR Bluetooth permission denied (System Settings > Privacy & Security > Bluetooth)")
        case .poweredOff: die(1, "ERROR Bluetooth is turned off")
        case .unsupported: die(1, "ERROR Bluetooth LE is not supported")
        default: break
        }
    }

    func start() {
        if !wake, let p = central.retrieveConnectedPeripherals(withServices: [serviceUUID]).first {
            connect(p)
            return
        }
        central.scanForPeripherals(withServices: nil, options: [CBCentralManagerScanOptionAllowDuplicatesKey: wake])
        if !wake {
            DispatchQueue.main.asyncAfter(deadline: .now() + 10) { [self] in
                if peripheral == nil { die(1, "ERROR ZEI not found nearby") }
            }
        }
    }

    func connect(_ p: CBPeripheral) {
        peripheral = p
        p.delegate = self
        central.connect(p, options: nil)
    }

    func centralManager(_ c: CBCentralManager, didDiscover p: CBPeripheral, advertisementData adv: [String: Any], rssi: NSNumber) {
        guard isZei(p.name, adv) else { return }
        if wake { die(0, "WOKE") }
        if peripheral != nil { return }
        c.stopScan()
        connect(p)
    }

    func centralManager(_ c: CBCentralManager, didConnect p: CBPeripheral) { p.discoverServices([serviceUUID]) }
    func centralManager(_ c: CBCentralManager, didFailToConnect p: CBPeripheral, error: Error?) {
        die(1, "ERROR Could not connect: \(error?.localizedDescription ?? "unknown")")
    }
    func centralManager(_ c: CBCentralManager, didDisconnectPeripheral p: CBPeripheral, error: Error?) { die(0, "DISCONNECTED") }

    func peripheral(_ p: CBPeripheral, didDiscoverServices error: Error?) {
        guard let s = p.services?.first(where: { $0.uuid == serviceUUID }) else { die(1, "ERROR ZEI service not found") }
        p.discoverCharacteristics([orientationUUID], for: s)
    }

    func peripheral(_ p: CBPeripheral, didDiscoverCharacteristicsFor s: CBService, error: Error?) {
        guard let ch = s.characteristics?.first(where: { $0.uuid == orientationUUID }) else { die(1, "ERROR Orientation characteristic not found") }
        orientation = ch
        p.setNotifyValue(true, for: ch)
        p.readValue(for: ch)
        out("CONNECTED")
        // Backup for dropped notifications.
        poll = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { _ in p.readValue(for: ch) }
    }

    func peripheral(_ p: CBPeripheral, didUpdateValueFor ch: CBCharacteristic, error: Error?) {
        if let b = ch.value?.first { out("SIDE \(b)") }
    }
}

guard CommandLine.arguments.count > 1, ["connect", "wake"].contains(CommandLine.arguments[1]) else {
    die(2, "ERROR usage: zei-ble connect|wake")
}
let helper = Helper(wake: CommandLine.arguments[1] == "wake")

Thread.detachNewThread {
    while readLine() != nil {}
    exit(0) // stdin closed: the parent is gone
}
withExtendedLifetime(helper) { RunLoop.main.run() }
