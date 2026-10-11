package com.contentmover.mobile;

public class AddressPolicyTest {
    static void require(boolean ok,String description){if(!ok)throw new AssertionError(description);}
    public static void main(String[] args){
        for(String host:new String[]{"10.79.181.211","172.16.1.2","172.31.255.254","192.168.1.5","169.254.1.1"})require(AddressPolicy.allowed(host,false),"LAN compatibility "+host);
        for(String host:new String[]{"100.64.0.1","100.90.80.70","100.127.255.254"}){require(AddressPolicy.allowed(host,true),"Remote address "+host);require(!AddressPolicy.allowed(host,false),"Remote requires explicit mode");}
        for(String host:new String[]{"100.63.255.255","100.128.0.1","100.100.100.100","8.8.8.8","127.0.0.1","172.32.1.1","256.1.1.1","example.com","100.90.80.70/evil","","::1"}){require(!AddressPolicy.allowed(host,true),"Reject remote "+host);require(!AddressPolicy.allowed(host,false),"Reject LAN "+host);}
        require(!AddressPolicy.allowed("192.168.1.2",true),"No LAN address in remote mode");
        System.out.println("PASS: LAN compatibility, explicit remote pairing, CGNAT boundaries, reserved/public/malformed address rejection");
    }
}
