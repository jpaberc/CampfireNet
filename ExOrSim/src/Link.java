import java.util.Random;

public class Link {

    int toId;
    double prob;

    public Link (int toId, double prob) {
        this.toId = toId;
        this.prob = prob;
    }

    public boolean sendOver (byte[] payload) {
        if (new Random().nextFloat() < this.prob) {
            // send it
            return true;
        }

        return false;
    }
}
