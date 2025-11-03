import socket
import threading
import json
import math
import time
import signal
import sys
import logging
from typing import Dict, Any
from collections import deque

from pythontuio import TuioClient, TuioListener

import cv2
import mediapipe as mp
import numpy as np

# CONFIG
TUIO_ADDR = ("0.0.0.0", 3333)
TCP_HOST = "127.0.0.1"
TCP_PORT = 8765

# TUIO SYMBOLS
ROTATE_SYMBOL = 0  # ID 0 for rotation
SELECT_SYMBOL = 1  # ID 1 for selection
BACK_SYMBOL = 12  # ID 12 for back
NURSE_MODE_SYMBOL = 13  # ID 13 for nurse mode activation
VIEW_PATIENT_INFO_SYMBOL = 14  # ID 14 for viewing patient info
EDIT_MEDICATIONS_SYMBOL = 15  # ID 15 for editing medications
MEDICATION_SYMBOLS = {
    1: "Paracetamol",
    2: "Amoxicillin",
    3: "Aspirin",
    4: "Metformin",
    5: "Lisinopril",
    6: "Atorvastatin",
}

NUM_WHEEL_SECTORS = len(MEDICATION_SYMBOLS)

# Global variables
clients = []
clients_lock = threading.Lock()
latest_objects: Dict[int, Dict[str, Any]] = {}
latest_objects_lock = threading.Lock()
running = True

# Gesture detection control
gesture_detection_enabled = False
gesture_detection_lock = threading.Lock()

# Setup logging
logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)


def broadcast(obj: Dict[str, Any]):
    data = (json.dumps(obj) + "\n").encode("utf-8")
    with clients_lock:
        dead = []
        sent_count = 0
        for client_socket in clients:
            try:
                client_socket.sendall(data)
                sent_count += 1
            except Exception as e:
                logger.warning(f"Failed to send to client: {e}")
                dead.append(client_socket)
        for dead_client in dead:
            clients.remove(dead_client)
        
        if sent_count > 0:
            logger.info(f"✓ Sent '{obj.get('type', 'unknown')}' to {sent_count} client(s)")
        elif len(clients) > 0:
            logger.warning(f"⚠ Could not send to any of {len(clients)} clients")


# TUIO LISTENER IMPLEMENTATION
class MyTuioListener(TuioListener):
    def __init__(self):
        super().__init__()

    def add_tuio_object(self, obj):
        self._handle_obj("add", obj)

    def update_tuio_object(self, obj):
        self._handle_obj("update", obj)

    def remove_tuio_object(self, obj):
        self._handle_obj("remove", obj)

    def refresh(self, time):
        pass

    def _handle_obj(self, event_type, obj):
        try:
            symbol_id = None

            possible_id_attrs = ['fiducial_id', 'symbol_id', 'class_id', 'pattern_id']
            for attr in possible_id_attrs:
                if hasattr(obj, attr):
                    symbol_id = getattr(obj, attr)
                    break

            if symbol_id is None:
                logger.warning(f"Could not find marker ID in object. Available attributes: {dir(obj)}")
                return

            session_id = getattr(obj, 'session_id', None)

            x = getattr(obj, 'x', getattr(obj, 'xpos', getattr(obj, 'x_pos', 0.5)))
            y = getattr(obj, 'y', getattr(obj, 'ypos', getattr(obj, 'y_pos', 0.5)))
            angle = getattr(obj, 'angle', 0)

            record = {
                "event": event_type,
                "symbol_id": symbol_id,
                "session_id": session_id,
                "x": x,
                "y": y,
                "angle": angle,
            }

            with latest_objects_lock:
                if event_type in ("add", "update"):
                    latest_objects[session_id] = record
                else:
                    latest_objects.pop(session_id, None)

            process_logic_and_broadcast(record)

        except Exception as e:
            logger.error(f"Error handling TUIO object: {e}")


def process_logic_and_broadcast(record: Dict[str, Any]):
    sid = record["symbol_id"]
    evt = record["event"]

    broadcast({"type": "tuio_obj", "payload": record})

    # WHEEL LOGIC - PATIENT MODE
    if sid == ROTATE_SYMBOL and evt == "add":
        broadcast({"type": "wheel_open", "x": record["x"], "y": record["y"], "marker": "patient"})

    if sid == ROTATE_SYMBOL and evt in ("add", "update"):
        # Calculate wheel sector based on rotation angle
        theta = record["angle"] % (2 * math.pi)
        frac = theta / (2 * math.pi)
        sector = int(frac * NUM_WHEEL_SECTORS) % NUM_WHEEL_SECTORS

        # Get medication name for this sector
        medication_name = list(MEDICATION_SYMBOLS.values())[sector]

        broadcast({
            "type": "wheel_hover",
            "sector": sector,
            "angle": theta,
            "x": record["x"],
            "y": record["y"],
            "medication": medication_name,
            "marker": "patient"
        })

        # SELECTION LOGIC
        with latest_objects_lock:
            for session_id, srec in list(latest_objects.items()):
                if srec["symbol_id"] == SELECT_SYMBOL:
                    dx = srec["x"] - record["x"]
                    dy = srec["y"] - record["y"]
                    dist = math.hypot(dx, dy)
                    if dist < 0.08:
                        selected_med = list(MEDICATION_SYMBOLS.values())[sector]
                        broadcast({
                            "type": "wheel_select_confirm",
                            "sector": sector,
                            "medication": selected_med,
                            "marker": "patient"
                        })

    # WHEEL LOGIC - NURSE MODE
    if sid == NURSE_MODE_SYMBOL and evt == "add":
        broadcast({"type": "nurse_wheel_open", "x": record["x"], "y": record["y"]})

    if sid == NURSE_MODE_SYMBOL and evt in ("add", "update"):
        # Calculate wheel sector based on rotation angle (same as patient mode)
        theta = record["angle"] % (2 * math.pi)
        frac = theta / (2 * math.pi)
        sector = int(frac * NUM_WHEEL_SECTORS) % NUM_WHEEL_SECTORS

        # Get medication name for this sector
        medication_name = list(MEDICATION_SYMBOLS.values())[sector]

        broadcast({
            "type": "nurse_wheel_hover",
            "sector": sector,
            "angle": theta,
            "x": record["x"],
            "y": record["y"],
            "medication": medication_name
        })

        # SELECTION LOGIC
        with latest_objects_lock:
            for session_id, srec in list(latest_objects.items()):
                # PATIENT SELECTION LOGIC
                if srec["symbol_id"] == VIEW_PATIENT_INFO_SYMBOL:
                    dx = srec["x"] - record["x"]
                    dy = srec["y"] - record["y"]
                    dist = math.hypot(dx, dy)
                    if dist < 0.08:
                        selected_item = list(MEDICATION_SYMBOLS.values())[sector]
                        broadcast({
                            "type": "nurse_wheel_select_confirm",
                            "sector": sector,
                            "item": selected_item
                        })
                
                # MEDICATION SELECTION LOGIC
                elif srec["symbol_id"] == EDIT_MEDICATIONS_SYMBOL:
                    dx = srec["x"] - record["x"]
                    dy = srec["y"] - record["y"]
                    dist = math.hypot(dx, dy)
                    if dist < 0.08:
                        selected_med = list(MEDICATION_SYMBOLS.values())[sector]
                        broadcast({
                            "type": "nurse_edit_med_select",
                            "sector": sector,
                            "medication": selected_med
                        })

    # BACK LOGIC
    if sid == BACK_SYMBOL and evt == "add":
        # Disable gesture detection when exiting
        global gesture_detection_enabled
        with gesture_detection_lock:
            gesture_detection_enabled = False
        broadcast({"type": "back_pressed"})

    # GESTURE DETECTION LOGIC - EDIT MEDICATIONS MODE
    if sid == EDIT_MEDICATIONS_SYMBOL and evt == "add":
        # NURSE MODE LOGIC
        marker13_nearby = False
        with latest_objects_lock:
            for session_id, srec in list(latest_objects.items()):
                if srec["symbol_id"] == NURSE_MODE_SYMBOL:
                    dx = srec["x"] - record["x"]
                    dy = srec["y"] - record["y"]
                    dist = math.hypot(dx, dy)
                    if dist < 0.08:
                        marker13_nearby = True
                        break
        
        # TOGGLE GESTURE DETECTION
        if not marker13_nearby:
            with gesture_detection_lock:
                gesture_detection_enabled = True
            broadcast({
                "type": "gesture_mode_toggled",
                "enabled": gesture_detection_enabled
            })


# GESTURE RECOGNITION (MediaPipe and $1)
class Point:
    def __init__(self, x, y):
        self.x = x
        self.y = y

class DollarRecognizer:
    def __init__(self, num_points=64):
        self.num_points = num_points
        self.last_gesture_time = 0
        self.points = []
        self.templates = self._create_templates()
        
    def _create_templates(self):
        templates = {}
        
        # LEFT SWIPE TEMPLATE
        left_points = [Point(1.0 - i/10.0, 0.5) for i in range(11)]
        templates['left'] = self._resample(left_points, self.num_points)
        
        # Right swipe template (horizontal line from left to right)
        right_points = [Point(i/10.0, 0.5) for i in range(11)]
        templates['right'] = self._resample(right_points, self.num_points)
        
        return templates
    
    def _resample(self, points, n):
        if len(points) < 2:
            return points
            
        interval = self._path_length(points) / (n - 1)
        d = 0
        new_points = [points[0]]
        
        i = 1
        while i < len(points):
            d1 = self._distance(points[i-1], points[i])
            if (d + d1) >= interval:
                qx = points[i-1].x + ((interval - d) / d1) * (points[i].x - points[i-1].x)
                qy = points[i-1].y + ((interval - d) / d1) * (points[i].y - points[i-1].y)
                q = Point(qx, qy)
                new_points.append(q)
                points.insert(i, q)
                d = 0
            else:
                d += d1
            i += 1
        
        if len(new_points) == n - 1:
            new_points.append(points[-1])
            
        return new_points
    
    def _path_length(self, points):
        length = 0
        for i in range(1, len(points)):
            length += self._distance(points[i-1], points[i])
        return length
    
    def _distance(self, p1, p2):
        return math.sqrt((p1.x - p2.x)**2 + (p1.y - p2.y)**2)
    
    def _path_distance(self, pts1, pts2):
        if len(pts1) != len(pts2):
            return float('inf')
        d = 0
        for i in range(len(pts1)):
            d += self._distance(pts1[i], pts2[i])
        return d / len(pts1)
        
    def recognize(self):
        if len(self.points) < 5:
            return None
        
        # Resample the input gesture
        points = self._resample(self.points, self.num_points)
        
        # Find the best matching template
        best_score = float('inf')
        best_gesture = None
        scores = {}
        
        for name, template in self.templates.items():
            distance = self._path_distance(points, template)
            scores[name] = distance
            if distance < best_score:
                best_score = distance
                best_gesture = name
        
        logger.info(f"  Scores - Left: {scores['left']:.3f}, Right: {scores['right']:.3f}, Best: {best_gesture} ({best_score:.3f})")
        
        # If the match is good enough (threshold)
        if best_score < 0.5:
            self.last_gesture_time = time.time()
            self.points = []
            return best_gesture
        else:
            logger.info(f"  No match (threshold=0.5, best score={best_score:.3f})")
        
        return None


def hand_tracking_thread():
    global running, gesture_detection_enabled
    
    logger.info("Hand tracking with MediaPipe ready")
    
    try:
        # Initialize MediaPipe Hands
        mp_hands = mp.solutions.hands
        mp_drawing = mp.solutions.drawing_utils
        
        # Time adjustment tracking (continuous position-based control)
        base_time_minutes = 450  # Starting time: 7:30 AM = 450 minutes from midnight
        current_time_minutes = base_time_minutes
        last_broadcast_time = 0
        broadcast_cooldown = 0.5  # Broadcast updates every 0.5 seconds
        
        cap = None
        hands = None
        gesture_active = False
        frame_count = 0
        
        while running:
            # Check if gesture detection is enabled
            with gesture_detection_lock:
                is_enabled = gesture_detection_enabled
            
            # If not enabled and camera is open, close it
            if not is_enabled:
                if gesture_active:
                    logger.info("Gesture detection DISABLED - closing camera")
                    
                    # Broadcast final time before closing
                    hours = current_time_minutes // 60
                    minutes = current_time_minutes % 60
                    time_str = f"{hours:02d}:{minutes:02d}"
                    broadcast({
                        "type": "gesture_time_final",
                        "time": time_str,
                        "minutes": current_time_minutes
                    })
                    
                    if cap is not None:
                        cap.release()
                        cv2.destroyAllWindows()
                        cap = None
                    gesture_active = False
                time.sleep(0.1)
                continue
            
            # If enabled and camera is not open, open it
            if is_enabled and cap is None:
                logger.info("✓ Gesture detection ENABLED - opening camera...")
                
                # Start with camera 1 and 2, then try 0 as last resort
                camera_indices_to_try = [1, 2, 0, 3]
                
                for camera_index in camera_indices_to_try:
                    logger.info(f"Attempting to open camera index {camera_index}...")
                    try:
                        cap = cv2.VideoCapture(camera_index, cv2.CAP_DSHOW)  # DirectShow on Windows
                        time.sleep(0.5)  # Give it time to initialize
                        
                        if cap.isOpened():
                            ret, test_frame = cap.read()
                            if ret and test_frame is not None:
                                logger.info(f"✓ SUCCESS! Camera {camera_index} opened and working!")
                                break
                            else:
                                cap.release()
                                cap = None
                        else:
                            logger.warning(f"✗ Camera {camera_index} failed to open")
                            if cap:
                                cap.release()
                            cap = None
                    except Exception as e:
                        logger.warning(f"Camera {camera_index} error: {e}")
                        if cap:
                            cap.release()
                        cap = None
                
                if cap is None or not cap.isOpened():
                    logger.error("The system will retry in 5 seconds...")
                    time.sleep(5)  # Wait before retrying
                    continue
                
                # Set camera properties
                cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
                cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
                
                # Initialize MediaPipe Hands
                hands = mp_hands.Hands(
                    min_detection_confidence=0.5,
                    min_tracking_confidence=0.5,
                    max_num_hands=1
                )
                
                logger.info("✓ Hand tracking ACTIVE - move hand left/right to adjust time")
                logger.info("  Close your palm (fist) to save and exit")
                gesture_active = True
                frame_count = 0
                
                # Reset time when camera opens
                base_time_minutes = 450  # 7:30 AM
                current_time_minutes = base_time_minutes
            
            # Process camera frames
            if cap is not None and hands is not None:
                success, image = cap.read()
                if not success:
                    logger.warning("Failed to read frame, will retry...")
                    time.sleep(0.1)
                    continue
                
                frame_count += 1
                
                # Flip and convert image
                image = cv2.flip(image, 1)
                image_rgb = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
                
                # Process hand detection
                results = hands.process(image_rgb)
                
                if results.multi_hand_landmarks:
                    for hand_landmarks in results.multi_hand_landmarks:
                        # Get wrist position (landmark 0) for X tracking
                        wrist = hand_landmarks.landmark[0]
                        hand_x = wrist.x  # 0.0 (left) to 1.0 (right)
                        
                        # Detect closed palm (fist gesture)
                        # Compare fingertip distances to palm base
                        thumb_tip = hand_landmarks.landmark[4]
                        index_tip = hand_landmarks.landmark[8]
                        middle_tip = hand_landmarks.landmark[12]
                        ring_tip = hand_landmarks.landmark[16]
                        pinky_tip = hand_landmarks.landmark[20]
                        palm_base = hand_landmarks.landmark[0]  # wrist as reference
                        
                        # Calculate distances from fingertips to palm
                        def distance_3d(p1, p2):
                            return math.sqrt((p1.x - p2.x)**2 + (p1.y - p2.y)**2 + (p1.z - p2.z)**2)
                        
                        thumb_dist = distance_3d(thumb_tip, palm_base)
                        index_dist = distance_3d(index_tip, palm_base)
                        middle_dist = distance_3d(middle_tip, palm_base)
                        ring_dist = distance_3d(ring_tip, palm_base)
                        pinky_dist = distance_3d(pinky_tip, palm_base)
                        
                        # Average distance - if all fingers are close to palm, it's a fist
                        avg_distance = (thumb_dist + index_dist + middle_dist + ring_dist + pinky_dist) / 5
                        is_fist = avg_distance < 0.15  # Threshold for closed palm
                        is_closing = avg_distance < 0.20  # Getting close to closing
                        
                        if is_fist:
                            logger.info("✓ CLOSED PALM detected - saving time and closing camera")
                            
                            # First, send the final time to C#
                            hours = current_time_minutes // 60
                            minutes = current_time_minutes % 60
                            time_str = f"{hours:02d}:{minutes:02d}"
                            logger.info(f"✓ Final time selected: {time_str}")
                            
                            # Check if clients are connected
                            with clients_lock:
                                client_count = len(clients)
                            
                            if client_count > 0:
                                # Send final time
                                broadcast({
                                    "type": "gesture_time_final",
                                    "time": time_str,
                                    "minutes": current_time_minutes
                                })
                                
                                time.sleep(0.2) 
                                
                                broadcast({
                                    "type": "gesture_mode_toggled",
                                    "enabled": False
                                })
                            
                            time.sleep(0.3)  # Give time for messages to send
                            
                            with gesture_detection_lock:
                                gesture_detection_enabled = False
                            continue
                        
                        # Map hand position to time adjustment
                        # Center (0.5) = base time, left = decrease, right = increase
                        # Full range: left edge = -4 hours, right edge = +4 hours
                        max_adjustment_minutes = 240  # ±4 hours
                        time_offset = (hand_x - 0.5) * 2 * max_adjustment_minutes
                        current_time_minutes = base_time_minutes + int(time_offset)
                        
                        # Keep time in valid 24-hour range
                        current_time_minutes = max(0, min(1439, current_time_minutes))  # 0-1439 = 00:00-23:59
                        
                        # Convert to hours and minutes for display
                        hours = current_time_minutes // 60
                        minutes = current_time_minutes % 60
                        time_str = f"{hours:02d}:{minutes:02d}"
                        
                        # Broadcast time updates periodically
                        current_time = time.time()
                        if current_time - last_broadcast_time >= broadcast_cooldown:
                            broadcast({
                                "type": "gesture_time_update",
                                "time": time_str,
                                "minutes": current_time_minutes
                            })
                            last_broadcast_time = current_time
                        
                        # Draw hand landmarks
                        mp_drawing.draw_landmarks(
                            image, hand_landmarks, mp_hands.HAND_CONNECTIONS)
                        
                        # Add visual feedback - show current time
                        time_color = (0, 255, 0) if not is_closing else (0, 165, 255)  # Green or Orange
                        cv2.putText(image, f"TIME: {time_str}", (150, 80), 
                                   cv2.FONT_HERSHEY_SIMPLEX, 2, time_color, 4)
                        cv2.putText(image, "Move LEFT to decrease, RIGHT to increase", (10, 30), 
                                   cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 0), 2)
                        
                        # Add instruction for closing palm with visual feedback
                        if is_closing:
                            cv2.putText(image, f">>> CLOSING PALM - WILL SAVE! <<<", (10, 120), 
                                       cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 100, 255), 2)
                        else:
                            cv2.putText(image, f"Close palm to save & exit", (10, 120), 
                                       cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 255), 2)
                        
                        # Draw position indicator bar
                        bar_y = 450
                        bar_width = 600
                        bar_x_start = 20
                        cv2.rectangle(image, (bar_x_start, bar_y - 10), (bar_x_start + bar_width, bar_y + 10), 
                                     (100, 100, 100), -1)
                        hand_pos_x = int(bar_x_start + hand_x * bar_width)
                        cv2.circle(image, (hand_pos_x, bar_y), 15, (0, 255, 255), -1)
                
                # Show camera preview window
                cv2.imshow('Hand Tracking - Time Adjustment (Close palm to save)', image)
                key = cv2.waitKey(1) & 0xFF
                if key == ord('q'):
                    with gesture_detection_lock:
                        gesture_detection_enabled = False
                    continue
                
                time.sleep(0.03)  # ~30 FPS
        
        # Cleanup
        if cap is not None:
            cap.release()
        if hands is not None:
            hands.close()
        cv2.destroyAllWindows()
        
    except Exception as e:
        logger.error(f"✗ Error in hand tracking thread: {e}")
        import traceback
        traceback.print_exc()


def client_reader_thread(conn: socket.socket, addr):
    client_name = f"{addr[0]}:{addr[1]}"
    logger.info(f"Client connected: {client_name}")

    try:
        conn.settimeout(0.5)
        while running:
            try:
                data = conn.recv(1024)
                if not data:
                    break
            except socket.timeout:
                continue
            except Exception:
                break
    except Exception as e:
        logger.error(f"Error in client thread: {e}")
    finally:
        with clients_lock:
            if conn in clients:
                clients.remove(conn)
        try:
            conn.close()
        except:
            pass
        logger.info(f"Client disconnected: {client_name}")

def tcp_acceptor(host: str, port: int):
    logger.info(f"Starting TCP server on {host}:{port}")

    server_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server_sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server_sock.bind((host, port))
    server_sock.listen(8)
    server_sock.settimeout(1.0)

    try:
        while running:
            try:
                conn, addr = server_sock.accept()
            except socket.timeout:
                continue
            except OSError:
                break

            with clients_lock:
                clients.append(conn)

            client_thread = threading.Thread(target=client_reader_thread, args=(conn, addr), daemon=True)
            client_thread.start()

    finally:
        server_sock.close()


def start_tuio_client(tuio_addr):
    try:
        client = TuioClient(tuio_addr)
        listener = MyTuioListener()
        client.add_listener(listener)

        logger.info(f"✓ TUIO Client started on {tuio_addr}")

        # This blocks until client stops
        client.start()

    except Exception as e:
        logger.error(f"✗ TUIO client error: {e}")


def shutdown(signum=None, frame=None):
    global running
    logger.info("Shutting down...")
    running = False

    with clients_lock:
        for client_socket in list(clients):
            try:
                client_socket.close()
            except:
                pass
        clients.clear()

    time.sleep(0.3)
    sys.exit(0)

def main():
    signal.signal(signal.SIGINT, shutdown)
    signal.signal(signal.SIGTERM, shutdown)

    # Start TCP acceptor thread
    tcp_thread = threading.Thread(target=tcp_acceptor, args=(TCP_HOST, TCP_PORT), daemon=True)
    tcp_thread.start()

    # Start TUIO client thread
    tuio_thread = threading.Thread(target=start_tuio_client, args=(TUIO_ADDR,), daemon=True)
    tuio_thread.start()

    # Start hand tracking thread if MediaPipe
    gesture_thread = threading.Thread(target=hand_tracking_thread, daemon=True)
    gesture_thread.start()

    logger.info("✓ Waiting for connections...")
    logger.info("  Connect your C# client to tcp://127.0.0.1:8765")

    try:
        while running:
            time.sleep(0.5)
    except KeyboardInterrupt:
        shutdown()

if __name__ == "__main__":
    main()