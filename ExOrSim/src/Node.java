import java.util.*;

public class Node {
    int id;
    private ArrayList<Link> links;

    // buffer of successfully received packets
    public ArrayList<ExOrPacket> packets;

    // copy of priortized forward list from packet
    private List<Node> forward_list;
    // time at which the node predicts it should start forwarding packets
    private int forward_timer;


    private int last_packet_num;
    private long last_packet_timestamp;
    // measured rate at which the currently sending node is sending
    private double transmission_rate;
    // expected num packets left for the sender to send
    private int num_packets_left;

    // highest-priority node known to have received a copy of packet i
    private Map<Integer, Node> batch_map;

    public Node (int id) {
        this.id = id;
        this.links = new ArrayList<>();
        this.packets = new ArrayList<>();
        this.forward_list = new ArrayList<>();
        this.batch_map = new HashMap<>();
    }

    public void addLink(Node toNode, double prob) {
        links.add(new Link(toNode, prob));
    }

    public void send(ExOrPacket packet) {
        for (Link link : links) {
            if (new Random().nextFloat() < link.prob) {
                link.toNode.receive(packet);
            }
        }
    }

    public void flush() {
        for (ExOrPacket packet : packets) {
            packet.batch_map = this.batch_map;
            send(packet);
        }
    }

    public void receive(ExOrPacket packet) {

        // only participate if this node is in the forward list for the batch
        if (packet.forward_list.contains(this)) {
            boolean seen = false;
            for (ExOrPacket seenPacket : this.packets) {
                if (packet.packet_num == seenPacket.packet_num) {
                    seen = true;
                }
            }
            if (!seen) { packets.add(packet); }

            this.forward_list = packet.forward_list;
            // If the entry indicates a higher priority node than previously seen,
            // update the entry in this node's batch map
            for (Map.Entry<Integer, Node> entry : packet.batch_map.entrySet()) {
                Node known_value = batch_map.get(entry.getKey());

                Node new_value = entry.getValue();
                if (known_value != null) {
                    if (forward_list.indexOf(new_value) < forward_list.indexOf(known_value)) {
                        batch_map.put(entry.getKey(), new_value);
                    }
                } else {
                    batch_map.put(entry.getKey(), new_value);
                }
            }

            // update idea of transmission rate
            if (!packets.isEmpty()) {
                if (this.last_packet_num < packet.packet_num) {
                    long receive_time = System.nanoTime();
                    int elapsed_packets = packet.packet_num - this.last_packet_num;
                    long elapsed_time = receive_time - this.last_packet_timestamp;

                    this.transmission_rate = 0.9 * this.transmission_rate + 0.1 * (elapsed_packets / elapsed_time);

                    this.last_packet_num = packet.packet_num;
                    this.last_packet_timestamp = receive_time;
                }
            } else {
                this.last_packet_timestamp = System.nanoTime();
                this.last_packet_num = packet.packet_num;
            }
        }
    }
}
