# Mock Linux Detection Server

用于本地联调 Windows 客户端的最小 gRPC server。

## 1) 安装依赖

```bash
pip install -r requirements.txt
```

## 2) 生成 Python gRPC 代码

在仓库根目录执行：

```bash
python -m grpc_tools.protoc ^
  -I ./src/MeetingAgent.Transport/Protos ^
  --python_out=./tools/mock-server ^
  --grpc_python_out=./tools/mock-server ^
  ./src/MeetingAgent.Transport/Protos/detection_agent.proto
```

将生成：

- `tools/mock-server/detection_agent_pb2.py`
- `tools/mock-server/detection_agent_pb2_grpc.py`

## 3) 启动 mock server

```bash
python ./tools/mock-server/mock_detection_server.py
```

默认监听 `0.0.0.0:50051`。
