// Times Core ML's own load of a compiled model (.mlmodelc), without ONNX
// Runtime: how much of a CoreML session's start is Core ML specialising the
// model for the device. CI runs it on the models ONNX Runtime cached.
//
//     swift coreml_load.swift <compiled_model.mlmodelc> [cpuOnly|cpuAndGPU|all]...

import CoreML
import Foundation

let args = CommandLine.arguments
guard args.count >= 2 else {
    print("usage: swift coreml_load.swift <compiled_model.mlmodelc> [cpuOnly|cpuAndGPU|all]...")
    exit(2)
}

let url = URL(fileURLWithPath: args[1])
let names = args.count > 2 ? Array(args[2...]) : ["cpuOnly", "cpuAndGPU"]
let units: [String: MLComputeUnits] = ["cpuOnly": .cpuOnly, "cpuAndGPU": .cpuAndGPU, "all": .all]

for name in names {
    guard let unit = units[name] else { print("unknown compute units \(name)"); exit(2) }
    let config = MLModelConfiguration()
    config.computeUnits = unit
    let start = Date()
    do {
        let model = try MLModel(contentsOf: url, configuration: config)
        let seconds = Date().timeIntervalSince(start)
        print("\(name): MLModel load \(String(format: "%.1f", seconds)) s (\(model.modelDescription.inputDescriptionsByName.count) inputs)")
    } catch {
        print("\(name): load failed after \(Date().timeIntervalSince(start)) s: \(error)")
    }
}
if #available(macOS 14.0, *) {
    print("GPU devices visible to Core ML: \(MLAllComputeDevices().map { "\($0)" })")
}
