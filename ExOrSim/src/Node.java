import java.util.ArrayList;

public class Node {
    int id;
    private ArrayList<Link> links;

    public Node () {
        links = new ArrayList<>();
    }

    public void addLink(int toId, double prob) {
        links.add(new Link(toId, prob));
    }

    public void traditionalSend(byte[] payload) {
        if ()
    }
}
