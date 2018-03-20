import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

public class Main {

    public static void main(String [] args) {
        // configure a network
        Node a = new Node(0);
        Node b = new Node(1);
        Node c = new Node(2);

        a.addLink(b, 0.8);
        a.addLink(c, 0.1);
        b.addLink(a, 0.8);
        b.addLink(c, 0.9);
        c.addLink(b, 0.9);
        c.addLink(a, 0.1);



        Node src = a;
        Node dst = c;
        // create a batch
        int batch_size = 10;

        ArrayList<ExOrPacket> batch = new ArrayList<>();
        List<Node> forward_list = new ArrayList<>();
        forward_list.add(b);
        forward_list.add(c);
        Map<Integer, Node> batch_map = new HashMap<>();
        for (int i = 0; i < batch_size; i++) {
            batch_map.put(i, src);
        }

        for (int i = 0; i < 10; i++) {
            batch.add(new ExOrPacket(0, i, batch_size, 0, 0, forward_list, batch_map, "hello".getBytes()));
        }

        // send the packets
        for (ExOrPacket packet : batch) {
            src.send(packet);
        }

        for (Node n : forward_list) {
            n.flush();
        }

        for (ExOrPacket packet : dst.packets) {
            System.out.println(packet.packet_num);
        }
    }
}
