import java.util.Random;

public class Link {

    Node toNode;
    double prob;

    public Link (Node toNode, double prob) {
        this.toNode = toNode;
        this.prob = prob;
    }
}
