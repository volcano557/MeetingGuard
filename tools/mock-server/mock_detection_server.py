import time
from concurrent import futures

import grpc

import detection_agent_pb2 as pb2
import detection_agent_pb2_grpc as pb2_grpc


class DetectionAgentService(pb2_grpc.DetectionAgentServiceServicer):
    def Stream(self, request_iterator, context):
        session_id = ""
        for req in request_iterator:
            payload = req.WhichOneof("payload")
            if payload == "session_start":
                session_id = req.session_start.session_id
                print(f"[session_start] session={session_id}")
                yield pb2.ServerEnvelope(
                    pong=pb2.HealthPongMessage(
                        session_id=session_id,
                        ping_id=0,
                        client_send_utc_ms=int(time.time() * 1000),
                        server_recv_utc_ms=int(time.time() * 1000),
                        server_send_utc_ms=int(time.time() * 1000),
                    )
                )
            elif payload == "video_frame":
                vf = req.video_frame
                # mock：每收到一帧都返回一个结果，便于 Windows UI 联调
                yield pb2.ServerEnvelope(
                    detection_result=pb2.DetectionResultMessage(
                        session_id=vf.session_id,
                        frame_id=vf.frame_id,
                        timestamp_utc_ms=int(time.time() * 1000),
                        risk_level=pb2.RiskLevelProto.RISK_LEVEL_LOW,
                        risk_type="mock_visual",
                        message=f"mock result for frame {vf.frame_id}",
                        score=0.42,
                        source="visual",
                    )
                )
            elif payload == "audio_chunk":
                pass
            elif payload == "ping":
                p = req.ping
                now = int(time.time() * 1000)
                yield pb2.ServerEnvelope(
                    pong=pb2.HealthPongMessage(
                        session_id=p.session_id,
                        ping_id=p.ping_id,
                        client_send_utc_ms=p.client_send_utc_ms,
                        server_recv_utc_ms=now,
                        server_send_utc_ms=now,
                    )
                )
            elif payload == "session_stop":
                print(f"[session_stop] session={req.session_stop.session_id}")
                return


def main():
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=8))
    pb2_grpc.add_DetectionAgentServiceServicer_to_server(DetectionAgentService(), server)
    server.add_insecure_port("[::]:50051")
    print("mock detection server listening on :50051")
    server.start()
    server.wait_for_termination()


if __name__ == "__main__":
    main()
