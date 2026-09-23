package io.ledgercore.policy;

import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.boot.context.properties.ConfigurationPropertiesScan;

@SpringBootApplication
@ConfigurationPropertiesScan
public class PolicyServiceApplication {

  public static void main(String[] args) {
    SpringApplication.run(PolicyServiceApplication.class, args);
  }
}
