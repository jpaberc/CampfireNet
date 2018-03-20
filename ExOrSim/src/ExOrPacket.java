import java.util.List;
import java.util.Map;

public class ExOrPacket {
    int batchId;
    int dst;
    int packet_num;
    int batch_size;
    int frag_num;
    int frag_size;
    List<Node> forward_list;
    Map<Integer, Node> batch_map;
    byte[] payload;

    public ExOrPacket (int batchId, int packet_num, int batch_size,
                       int frag_num, int frag_size, List<Node> forward_list,
                       Map<Integer, Node> batch_map, byte[] payload) {
        this.batchId = batchId;
        this.packet_num = packet_num;
        this.batch_size = batch_size;
        this.frag_num = frag_num;
        this.frag_size = frag_size;
        this.forward_list = forward_list;
        this.batch_map = batch_map;
        this.payload = payload;
    }
}
